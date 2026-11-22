using System;
using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Rules;

namespace Emberdelve.Flow
{
    /// <summary>Moves descending survivors to prepared floors and refills seats when the source closes.
    /// Each party arrives together after its members have entered the portal or died.</summary>
    public sealed class RoyaleDescentDriver
    {
        public delegate bool PrepareFloor(int floor, out string reason);
        private readonly RunDirector _director;
        private readonly PrepareFloor _prepare;
        private readonly Func<int, bool> _ready;
        private readonly Func<int, PlayerSession, bool> _teamArrivalReady;
        private readonly RoyalePopulationDriver.SpawnBot _spawn;
        private readonly int _durationSeconds;
        private readonly RoyalePopulationDriver[] _population = new RoyalePopulationDriver[4];

        public RoyaleDescentDriver(RunDirector director, int durationSeconds, PrepareFloor prepare,
            RoyalePopulationDriver.SpawnBot spawn, Func<int, bool> participantsReady,
            Func<int, PlayerSession, bool> teamArrivalReady = null)
        {
            _director = director; _prepare = prepare; _ready = participantsReady;
            _teamArrivalReady = teamArrivalReady;
            _spawn = spawn; _durationSeconds = durationSeconds;
            for (int floor = 2; floor <= 3; floor++)
                _population[floor] = new RoyalePopulationDriver(director, floor, durationSeconds, 0, spawn);
        }

        public bool Tick(float now, out string reason)
        {
            reason = "Waiting for descending survivors";
            if (NetRole.IsClientOnly || _director == null || !_director.IsStarted
                || _director.State != RunState.Running || !_director.RoyaleMode.HasValue
                || _prepare == null || _ready == null || !float.IsFinite(now)) return false;
            if (_director.HasTeamDescent) return TickTeamArrivals(out reason);
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
                bool localIsDescending = false;
                foreach (var session in _director.Players)
                {
                    if (session.Floor != source || session.HasResult || session.State != RunState.Running
                        || session.Rig == null || session.Rig.IsDead || session.Rig.IsExtracted) continue;
                    if (session.Rig.IsInFloorTransit && !session.AwaitingInitialFloor)
                    {
                        pending.Add(session);
                        localIsDescending |= session.IsLocal;
                    }
                    else sourceHasRunners = true;
                }
                bool soloDeparture = NetRole.IsOffline && !_director.IsServerMode && _director.RoyaleMode == RoyalePartyMode.Solo
                    && localIsDescending;
                if (pending.Count == 0) continue;
                var coordinator = _director.FloorCoordinator;
                if (coordinator.CopyPreparedPlan(floor) == null && !_prepare(floor, out reason)) return false;
                // Reserve everyone, including late teammates, before any filler can claim their seats.
                if (!coordinator.IsAdmissionClosed(floor))
                    foreach (var session in pending)
                        if (!coordinator.TryReserveIncoming(session.Rig, floor, out reason)) return false;
                if (remaining > 0 && sourceHasRunners && !soloDeparture)
                { reason = "Waiting for source floor survivors"; continue; }
                if (!_ready(floor)) { reason = "Waiting for destination map readiness"; continue; }
                if (!coordinator.TryActivate(floor, out reason)) return false;
                if (!_population[floor].Tick(now, true, out reason)) return false;
                if (soloDeparture) _director.CloseRoyaleFloor(source);
                coordinator.TryRetireSource(source, out _);
                advanced = true;
            }
            if (advanced) reason = null;
            return advanced;
        }

        private bool TickTeamArrivals(out string reason)
        {
            reason = null;
            bool advanced = false;
            var coordinator = _director.FloorCoordinator;
            for (int floor = 2; floor <= 3; floor++)
            {
                int source = floor - 1;
                var parties = new Dictionary<int, List<PlayerSession>>();
                var waitingParties = new HashSet<int>();
                var stagedBots = new List<PlayerSession>();
                foreach (var session in _director.Players)
                {
                    if (session.HasResult || session.State != RunState.Running || session.Rig == null
                        || session.Rig.IsDead || session.Rig.IsExtracted) continue;
                    if (session.Floor == floor && session.AwaitingInitialFloor) stagedBots.Add(session);
                    if (session.Floor > source) continue;
                    if (session.Floor != source || session.AwaitingInitialFloor || !session.Rig.IsPortalSpectating)
                    { waitingParties.Add(session.PartyId); continue; }
                    if (!parties.TryGetValue(session.PartyId, out var members))
                        parties.Add(session.PartyId, members = new List<PlayerSession>());
                    members.Add(session);
                }
                foreach (var party in parties)
                {
                    if (waitingParties.Contains(party.Key)) continue;
                    if (coordinator.CopyPreparedPlan(floor) == null && !_prepare(floor, out reason)) return advanced;
                    if (!party.Value.TrueForAll(session => IsTeamArrivalReady(floor, session)))
                    { reason = "Waiting for destination map readiness"; continue; }
                    if (!_director.TryGetRoyaleFloorClock(floor, out _, out _)
                        && !coordinator.TryOpenTeamDestination(floor, _durationSeconds, out reason)) return advanced;
                    if (coordinator.TryArriveParty(party.Value, floor, out string blocked)) advanced = true;
                    else reason = blocked;
                }
                if (!_director.TryGetRoyaleFloorClock(floor, out _, out _)) continue;
                foreach (var session in stagedBots)
                {
                    if (coordinator.TryArrive(session.Rig, floor, out string blocked)) advanced = true;
                    else reason = blocked;
                }
                coordinator.TryRetireSource(source, out _);
                if (!_director.IsRoyaleFloorOpen(floor) || (_director.IsRoyaleFloorOpen(source) && !coordinator.IsRetired(source)))
                    continue;
                coordinator.ReleaseUnusedTeamSeats(floor);
                while (_spawn != null && coordinator.TryGetVacantParty(floor, out int party))
                {
                    int before = coordinator.ReservedCount(floor);
                    if (!_spawn(floor, party, out var bot, out reason)) return advanced;
                    if (bot == null || coordinator.ReservedCount(floor) != before + 1)
                    { reason = "Bot factory did not reserve exactly one seat"; return advanced; }
                    if (!coordinator.TryArrive(bot, floor, out reason)) return advanced;
                }
            }
            // Prepare at most one map per idle tick so online clients normally ACK both maps well before portal use.
            for (int floor = 2; !advanced && reason == null && floor <= 3; floor++)
                if (coordinator.CopyPreparedPlan(floor) == null) { _prepare(floor, out reason); break; }
            return advanced;
        }

        private bool IsTeamArrivalReady(int floor, PlayerSession session) =>
            _teamArrivalReady != null ? _teamArrivalReady(floor, session) : _ready(floor);
    }
}
