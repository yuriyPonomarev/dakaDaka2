using System;
using Emberdelve.Net.Rules;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B2, client process, owner of a player object only (added by NetPlayerRig.OnNetworkSpawn): what the owner knows about its own
    /// skills. It stores ONLY what the server replicated: the owner-read <c>SkillState</c> variable (mana and per-slot cooldowns, a few
    /// times a second) and the reliable cast results. The mana number is never regenerated, spent or predicted here, and a refused
    /// request changes nothing, so the display cannot disagree with the server for longer than one update. The one derived value is the
    /// cooldown countdown between two updates (SkillViewModel), re-anchored by every update.
    ///
    /// The real HUD (HudController) does not exist in a client process yet (the scene's game master is switched off for clients), so a
    /// windowed client draws a small IMGUI overlay from this component; the <see cref="Changed"/> event is where a future HUD binds.
    /// Logs, for the proof gates (F3 numbers, invariant culture):
    ///   [client] skill-result seq=N slot=S outcome=Cast|Refused reason=CODE skill=ID mana=.. maxMana=.. cd0=.. cd1=.. dur0=.. dur1=..
    ///   [client] skill-view mana=.. maxMana=.. cd0=.. cd1=..      (the replicated variable; at most every half second)
    /// </summary>
    public sealed class NetOwnerSkillView : MonoBehaviour
    {
        private const float ViewLogSeconds = 0.5f;

        private NetPlayerRig _owner;
        private readonly SkillViewModel _model = new SkillViewModel();
        private readonly string[] _skillIds = new string[SkillNetLimits.ActiveSlots];
        private float _nextViewLog;
        private bool _viewLogPending;
        private string _lastResultText = "";

        public SkillViewModel Model => _model;
        public int ResultsReceived { get; private set; }
        public int CastResults { get; private set; }
        public int RefusedResults { get; private set; }

        /// <summary>Raised after every replicated update (state or result). A HUD binds here.</summary>
        public event Action Changed;

        public void Configure(NetPlayerRig owner)
        {
            _owner = owner;
        }

        /// <summary>The replicated state variable changed (server write).</summary>
        public void OnState(SkillStateWire wire)
        {
            _model.Apply(wire.ToData(), Time.unscaledTimeAsDouble);
            _viewLogPending = true;
            Changed?.Invoke();
        }

        /// <summary>The server answered one request (reliable, ordered).</summary>
        public void OnResult(SkillResultWire result)
        {
            ResultsReceived++;
            if (result.OutcomeKind == SkillCastOutcome.Cast) CastResults++; else RefusedResults++;

            SkillStateData state = result.State.ToData();
            _model.Apply(state, Time.unscaledTimeAsDouble);
            if (result.Slot < _skillIds.Length && !string.IsNullOrEmpty(result.SkillId)) _skillIds[result.Slot] = result.SkillId;

            _lastResultText = result.OutcomeKind == SkillCastOutcome.Cast ? "cast" : "refused: " + result.ReasonCode;
            Debug.Log(NetTuning.LogPrefix + "[client] skill-result seq=" + result.Sequence + " slot=" + result.Slot +
                      " outcome=" + result.OutcomeKind + " reason=" + result.ReasonCode + " skill=" + (string.IsNullOrEmpty(result.SkillId) ? "-" : result.SkillId) +
                      " mana=" + F(state.Mana) + " maxMana=" + F(state.MaxMana) + " cd0=" + F(state.Cooldown0) + " cd1=" + F(state.Cooldown1) +
                      " dur0=" + F(state.Duration0) + " dur1=" + F(state.Duration1));
            Changed?.Invoke();
        }

        private void Update()
        {
            if (!_viewLogPending || Time.unscaledTime < _nextViewLog) return;
            _nextViewLog = Time.unscaledTime + ViewLogSeconds;
            _viewLogPending = false;

            SkillStateData s = _model.State;
            Debug.Log(NetTuning.LogPrefix + "[client] skill-view mana=" + F(s.Mana) + " maxMana=" + F(s.MaxMana) +
                      " cd0=" + F(s.Cooldown0) + " cd1=" + F(s.Cooldown1));
        }

        private void OnGUI()
        {
            if (Application.isBatchMode || !_model.HasState || _owner == null || !_owner.IsSpawned) return;

            double now = Time.unscaledTimeAsDouble;
            string text = "Mana " + Mathf.RoundToInt(_model.Mana) + " / " + Mathf.RoundToInt(_model.MaxMana) + "  (server)";
            for (int slot = 0; slot < SkillNetLimits.ActiveSlots; slot++)
            {
                float cooldown = _model.CooldownRemaining(slot, now);
                string key = KeyLabel(slot);
                string id = string.IsNullOrEmpty(_skillIds[slot]) ? "skill " + (slot + 1) : _skillIds[slot];
                text += "\n[" + key + "] " + id + (cooldown > 0.05f ? "  " + cooldown.ToString("F1") + "s" : "  ready");
            }

            if (_lastResultText.Length > 0) text += "\nlast: " + _lastResultText;
            GUI.Box(new Rect(12f, Screen.height - 112f, 300f, 100f), text);
        }

        private string KeyLabel(int slot)
        {
            Emberdelve.Player.PlayerRig rig = _owner != null ? _owner.Rig : null;
            return rig != null && rig.Input != null ? rig.Input.SkillKeyLabel(slot) : (slot == 0 ? "F" : "G");
        }

        private static string F(float value) { return NetSkillServer.F(value); }
    }
}
