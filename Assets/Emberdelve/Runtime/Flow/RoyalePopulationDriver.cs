using System;
using Emberdelve.Core;
using Emberdelve.Player;

namespace Emberdelve.Flow
{
    /// <summary>Authority population barrier; transport supplies readiness and its normal bot factory.</summary>
    public sealed class RoyalePopulationDriver
    {
        public delegate bool SpawnBot(int floor, int party, out PlayerRig rig, out string reason);
        private readonly RunDirector _director;
        private readonly int _floor, _duration;
        private readonly float _admissionSeconds;
        private readonly SpawnBot _spawn;
        private float? _firstParticipantAt;
        public bool Started { get; private set; }

        public RoyalePopulationDriver(RunDirector director, int floor, int durationSeconds,
            float admissionSeconds, SpawnBot spawn)
        {
            _director = director; _floor = floor; _duration = durationSeconds;
            _admissionSeconds = Math.Max(0, admissionSeconds); _spawn = spawn;
        }

        public bool Tick(float now, bool participantsReady, out string reason)
        {
            reason = "Waiting for population";
            if (Started) { reason = null; return true; }
            if (NetRole.IsClientOnly || _director == null || !_director.IsStarted || !_director.RoyaleMode.HasValue
                || _spawn == null || float.IsNaN(now) || float.IsInfinity(now)) return false;
            var floors = _director.FloorCoordinator;
            if (floors.ReservedCount(_floor) == 0) { _firstParticipantAt = null; return false; }
            if (!_firstParticipantAt.HasValue) _firstParticipantAt = now;
            if (now - _firstParticipantAt.Value < _admissionSeconds || !participantsReady) return false;
            if (!floors.TryCloseAdmission(_floor)) return false;
            while (floors.TryGetVacantParty(_floor, out int party))
            {
                int before = floors.ReservedCount(_floor);
                if (!_spawn(_floor, party, out var rig, out reason)) return false;
                if (rig == null || floors.ReservedCount(_floor) != before + 1)
                { reason = "Bot factory did not reserve exactly one seat"; return false; }
            }
            Started = floors.TryStartPopulation(_floor, _duration, out reason);
            return Started;
        }
    }
}
