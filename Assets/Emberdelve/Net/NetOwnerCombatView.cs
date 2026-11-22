using System;
using System.Globalization;
using Emberdelve.Net.Rules;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B3, client process, owner of a player object only (added by NetPlayerRig.OnNetworkSpawn): what the owner knows about its own combat
    /// requests. It stores ONLY the server's answers (one reliable CombatResultRpc per request that passed the wire gate): accepted or refused,
    /// the reason code and, for a draw release, the charge the SERVER measured. A refused request changes nothing here or anywhere on the
    /// client; the owner's health arrives through the player's Vitals variable. Also drives the enemy-view summary line.
    ///   [client] combat-result seq=.. kind=.. outcome=Accepted|Refused reason=.. charge=..
    /// </summary>
    public sealed class NetOwnerCombatView : MonoBehaviour
    {
        private NetPlayerRig _owner;
        private Emberdelve.Feedback.DungeonAmbience _ambience;

        public int ResultsReceived { get; private set; }
        public int AcceptedResults { get; private set; }
        public int RefusedResults { get; private set; }
        public CombatResultWire LastResult { get; private set; }

        /// <summary>Raised after every server answer. A HUD or the sender binds here.</summary>
        public event Action<CombatResultWire> Resulted;

        public void Configure(NetPlayerRig owner)
        {
            _owner = owner;
            if (owner != null && !owner.IsServer)
            {
                var feedback = GetComponent<Emberdelve.Feedback.FeedbackDirector>();
                if (feedback == null) feedback = gameObject.AddComponent<Emberdelve.Feedback.FeedbackDirector>();
                feedback.BindPlayer(owner.Rig);
                _ambience = GetComponent<Emberdelve.Feedback.DungeonAmbience>();
                if (_ambience == null) _ambience = gameObject.AddComponent<Emberdelve.Feedback.DungeonAmbience>();
                _ambience.BindPlayer(owner.Rig);
            }
        }

        public void OnResult(CombatResultWire result)
        {
            ResultsReceived++;
            if (result.OutcomeKind == CombatOutcome.Accepted) AcceptedResults++; else RefusedResults++;
            LastResult = result;
            Debug.Log(NetTuning.LogPrefix + "[client] combat-result seq=" + result.Sequence + " kind=" + (CombatIntentKind)result.Kind +
                      " outcome=" + result.OutcomeKind + " reason=" + result.ReasonCode +
                      " charge=" + result.Charge.ToString("F3", CultureInfo.InvariantCulture));
            Resulted?.Invoke(result);
        }

        private void Update()
        {
            if (_owner == null || !_owner.IsSpawned) return;
            NetEnemyViews.TickSummary(Time.unscaledTime);
        }

        private void LateUpdate()
        {
            if (_owner == null || !_owner.IsSpawned || !_owner.IsOwner || _owner.IsServer) return;
            var rig = _owner.Rig;
            if (rig != null && rig.Loadout != null)
                rig.Loadout.SetGuardHeldVisual(_owner.GuardRaised.Value && !rig.IsDead);
        }

        private void OnDisable()
        {
            if (_ambience != null) _ambience.enabled = false;
            if (_owner != null && !_owner.IsServer && _owner.Rig != null && _owner.Rig.Loadout != null)
                _owner.Rig.Loadout.SetGuardHeldVisual(false);
        }
    }
}
