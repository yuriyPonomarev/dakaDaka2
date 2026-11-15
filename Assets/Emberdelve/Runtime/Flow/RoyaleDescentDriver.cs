using System;
using System.Collections.Generic;
using Emberdelve.Core;

namespace Emberdelve.Flow
{
    /// <summary>Collects a source floor's survivors before allowing destination bot fill and arrival.
    /// Transport readiness must include the destination map acknowledgement for every human survivor.</summary>
    public sealed class RoyaleDescentDriver
    {
        public delegate bool PrepareFloor(int floor, out string reason);
        private readonly RunDirector _director;
        private readonly PrepareFloor _prepare;
        private readonly Func<int, bool> _ready;
        private readonly RoyalePopulationDriver[] _population = new RoyalePopulationDriver[4];

        public RoyaleDescentDriver(RunDirector director, int durationSeconds, PrepareFloor prepare,
            RoyalePopulationDriver.SpawnBot spawn, Func<int, bool> participantsReady)
        {
            _director = director; _prepare = prepare; _ready = participantsReady;
            for (int floor = 2; floor <= 3; floor++)
                _population[floor] = new RoyalePopulationDriver(director, floor, durationSeconds, 0, spawn);
        }

        public bool Tick(float now, out string reason)
        {
            reason = "Waiting for descending survivors";
            if (NetRole.IsClientOnly || _director == null || !_director.IsStarted
                || _director.State != RunState.Running || !_director.RoyaleMode.HasValue
                || _prepare == null || _ready == null || !float.IsFinite(now)) return false;
            bool advanced = false;
            for (int floor = 2; floor <= 3; floor++)
            {
                // Retry after custody delays; starting a clock alone is insufficient while any
                // survivor still has its source identity (including actors held in transit).
                if (_director.TryGetRoyaleFloorClock(floor, out _, out _))
                    _director.FloorCoordinator.TryRetireSource(floor - 1, out _);
                if (_population[floor].Started) continue;
                int source = floor - 1;
                if (!_director.TryGetRoyaleFloorClock(source, out _, out float remaining)) continue;
                var pending = new List<PlayerSession>();
                bool sourceHasRunners = false;
                foreach (var session in _director.Players)
                {
                    if (session.Floor != source || session.HasResult || session.State != RunState.Running
                        || session.Rig == null || session.Rig.IsDead || session.Rig.IsExtracted) continue;
                    if (session.Rig.IsInFloorTransit && !session.AwaitingInitialFloor) pending.Add(session);
                    else sourceHasRunners = true;
                }
                if (pending.Count == 0) continue;
                var coordinator = _director.FloorCoordinator;
                if (coordinator.CopyPreparedPlan(floor) == null && !_prepare(floor, out reason)) return false;
                // Reserve everyone, including late teammates, before any filler can claim their seats.
                if (!coordinator.IsAdmissionClosed(floor))
                    foreach (var session in pending)
                        if (!coordinator.TryReserveIncoming(session.Rig, floor, out reason)) return false;
                if (remaining > 0 && sourceHasRunners)
                { reason = "Waiting for source floor survivors"; continue; }
                if (!_ready(floor)) { reason = "Waiting for destination map readiness"; continue; }
                if (!coordinator.TryActivate(floor, out reason)) return false;
                if (!_population[floor].Tick(now, true, out reason)) return false;
                coordinator.TryRetireSource(source, out _);
                advanced = true;
            }
            if (advanced) reason = null;
            return advanced;
        }
    }
}
