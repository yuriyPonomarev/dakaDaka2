using System;
using Emberdelve.Core;
using Emberdelve.Interaction;
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
    /// Windowed clients present server vitals, skills and clock here, alongside the owner's interaction prompt.
    /// The offline HudController remains on the scene's disabled game master; it never drives a client simulation.
    /// Logs, for the proof gates (F3 numbers, invariant culture):
    ///   [client] skill-result seq=N slot=S outcome=Cast|Refused reason=CODE skill=ID mana=.. maxMana=.. cd0=.. cd1=.. dur0=.. dur1=..
    ///   [client] skill-view mana=.. maxMana=.. cd0=.. cd1=..      (the replicated variable; at most every half second)
    /// </summary>
    public sealed class NetOwnerSkillView : MonoBehaviour, Emberdelve.UI.ICharacterManaView
    {
        private const float ViewLogSeconds = 0.5f;

        private NetPlayerRig _owner;
        private readonly SkillViewModel _model = new SkillViewModel();
        private readonly string[] _skillIds = new string[SkillNetLimits.ActiveSlots];
        private float _nextViewLog;
        private bool _viewLogPending;
        private RaidClockWire _raidClock;
        private double _raidClockReceivedAt;
        private bool _hasRaidClock;
        private int _lastRaidClockBand = -1;
        private Interactor _interactor;
        private string _toastText = "";
        private float _toastUntil;
        private bool _runEnded;
        private GUIStyle _raidClockStyle;

        private void OnEnable()
        {
            GameEvents.ToastRaised += OnToast;
            GameEvents.RunEnded += OnRunEnded;
        }

        private void OnDisable()
        {
            GameEvents.ToastRaised -= OnToast;
            GameEvents.RunEnded -= OnRunEnded;
        }

        private void OnToast(int playerId, string message)
        {
            if (_owner == null || _owner.Rig == null || playerId != _owner.Rig.PlayerId) return;
            _toastText = message ?? "";
            _toastUntil = Time.unscaledTime + 3f;
        }

        private void OnRunEnded(int playerId, RunResult result)
        {
            if (_owner != null && _owner.Rig != null && playerId == _owner.Rig.PlayerId) _runEnded = true;
        }

        public SkillViewModel Model => _model;
        public bool HasCharacterMana => _model.HasState;
        public float CharacterMana => _model.Mana;
        public float CharacterMaxMana => _model.MaxMana;
        public int ResultsReceived { get; private set; }
        public int CastResults { get; private set; }
        public int RefusedResults { get; private set; }

        /// <summary>Raised after every replicated update (state or result). A HUD binds here.</summary>
        public event Action Changed;

        public void Configure(NetPlayerRig owner)
        {
            _owner = owner;
            _interactor = owner != null ? owner.GetComponent<Interactor>() : null;
            _runEnded = false;
        }

        /// <summary>The replicated state variable changed (server write).</summary>
        public void OnState(SkillStateWire wire)
        {
            _model.Apply(wire.ToData(), Time.unscaledTimeAsDouble);
            _viewLogPending = true;
            Changed?.Invoke();
        }

        /// <summary>Applies a run-clock sample authored by the server. Local time only interpolates between snapshots.</summary>
        public void OnRaidClock(RaidClockWire wire)
        {
            if (!wire.IsValid) return;
            _raidClock = wire;
            _raidClockReceivedAt = Time.unscaledTimeAsDouble;
            _hasRaidClock = wire.Enabled;

            int band = !wire.Enabled ? 0 : wire.Expired ? 2 : wire.Warning ? 1 : 0;
            if (band != _lastRaidClockBand)
            {
                _lastRaidClockBand = band;
                Debug.Log(NetTuning.LogPrefix + "[client] raid-clock enabled=" + wire.Enabled + " remaining=" + F(wire.RemainingSeconds) +
                          " warning=" + wire.Warning + " expired=" + wire.Expired + " source=server");
            }
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

            if (result.OutcomeKind != SkillCastOutcome.Cast && _owner != null && _owner.Rig != null)
                OnToast(_owner.Rig.PlayerId, "Skill unavailable: " + result.ReasonCode);
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
            if (Application.isBatchMode || _owner == null || !_owner.IsSpawned || _runEnded) return;
            Matrix4x4 previous = GUI.matrix;
            float scale = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
            GUI.matrix = Matrix4x4.TRS(new Vector3((Screen.width - 1280f * scale) * 0.5f,
                (Screen.height - 720f * scale) * 0.5f), Quaternion.identity, Vector3.one * scale);
            try { DrawHud(); }
            finally { GUI.matrix = previous; }
        }

        private void DrawHud()
        {
            var rig = _owner.Rig;
            var inventory = GetComponent<NetInventoryClient>();
            bool menuOpen = inventory != null && inventory.IsOpen;
            if (menuOpen)
            {
                float remaining = _hasRaidClock ? Mathf.Max(0f, _raidClock.RemainingSeconds - (float)(Time.unscaledTimeAsDouble - _raidClockReceivedAt)) : 0f;
                string health = rig?.Health != null ? Mathf.CeilToInt(rig.Health.CurrentHealth) + " / " + Mathf.CeilToInt(rig.Health.MaxHealth) : "—";
                GUI.Box(new Rect(110f, 4f, 1060f, 24f), "Health  " + health + "     •     Mana  " + Mathf.CeilToInt(_model.State.Mana) + "     •     Raid  " + FormatClock(remaining));
                return;
            }
            if (!menuOpen && rig != null && !rig.IsDead)
            {
                GUI.Label(new Rect(632f, 350f, 20f, 22f), "+");
                string prompt = _interactor != null && _interactor.CurrentTarget != null
                    ? Interactor.PromptFor(rig, _interactor.CurrentTarget) : "";
                if (!string.IsNullOrEmpty(prompt))
                {
                    string key = rig.Input != null ? rig.Input.InteractKeyLabel : "E";
                    GUI.Box(new Rect(390f, 408f, 500f, 42f), "[" + key + "] " + prompt);
                    if (_interactor.HoldProgress > 0f)
                        DrawBar(new Rect(440f, 452f, 400f, 5f), _interactor.HoldProgress, new Color(0.9f, 0.7f, 0.3f));
                }
            }
            if (Time.unscaledTime < _toastUntil && _toastText.Length > 0)
                GUI.Box(new Rect(340f, 82f, 600f, 36f), _toastText);
            if (rig != null && rig.Health != null)
            {
                var health = rig.Health;
                GUI.Box(new Rect(16f, 560f, 320f, 48f), "Health " + Mathf.CeilToInt(health.CurrentHealth) + " / " + Mathf.CeilToInt(health.MaxHealth));
                DrawBar(new Rect(24f, 595f, 304f, 6f), health.MaxHealth > 0f ? health.CurrentHealth / health.MaxHealth : 0f,
                    new Color(0.75f, 0.2f, 0.16f));
            }

            if (_hasRaidClock)
            {
                float remaining = _raidClock.Expired ? 0f : Mathf.Max(0f,
                    _raidClock.RemainingSeconds - (float)(Time.unscaledTimeAsDouble - _raidClockReceivedAt));
                string heading = _raidClock.Expired ? "RAID EXPIRED" :
                    remaining <= 10f ? "FINAL SECONDS" : remaining <= 60f ? "EXTRACT SOON" : "RAID TIME";
                string clockText = heading + "\n" + FormatClock(remaining);
                Color previous = GUI.color;
                Rect panel = new Rect(1016f, 12f, 252f, 72f);
                GUI.color = new Color(0.07f, 0.06f, 0.05f, 0.92f);
                GUI.DrawTexture(panel, Texture2D.whiteTexture);
                GUI.color = Color.white;
                if (_raidClockStyle == null)
                    _raidClockStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter
                    };
                _raidClockStyle.normal.textColor = _raidClock.Expired || remaining <= 10f
                    ? new Color(1f, 0.72f, 0.58f) : remaining <= 60f
                    ? new Color(1f, 0.87f, 0.56f) : Color.white;
                GUI.Label(panel, clockText, _raidClockStyle);
                GUI.color = previous;
            }

            if (!_model.HasState) return;

            double now = Time.unscaledTimeAsDouble;
            string text = "Mana " + Mathf.RoundToInt(_model.Mana) + " / " + Mathf.RoundToInt(_model.MaxMana);
            for (int slot = 0; slot < SkillNetLimits.ActiveSlots; slot++)
            {
                float cooldown = _model.CooldownRemaining(slot, now);
                string key = KeyLabel(slot);
                string id = string.IsNullOrEmpty(_skillIds[slot]) ? "skill " + (slot + 1) : _skillIds[slot];
                if (rig != null && rig.Class != null && rig.Class.activeSkills != null && slot < rig.Class.activeSkills.Length && rig.Class.activeSkills[slot] != null)
                    id = rig.Class.activeSkills[slot].displayName;
                text += "\n[" + key + "] " + id + (cooldown > 0.05f ? "  " + cooldown.ToString("F1") + "s" : "  ready");
            }

            GUI.Box(new Rect(16f, 616f, 320f, 88f), text);
        }

        private static void DrawBar(Rect rect, float fraction, Color color)
        {
            Color previous = GUI.color;
            GUI.color = new Color(0.12f, 0.12f, 0.12f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            rect.width *= Mathf.Clamp01(fraction);
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private string KeyLabel(int slot)
        {
            Emberdelve.Player.PlayerRig rig = _owner != null ? _owner.Rig : null;
            return rig != null && rig.Input != null ? rig.Input.SkillKeyLabel(slot) : (slot == 0 ? "F" : "G");
        }

        private static string FormatClock(float seconds)
        {
            int total = Mathf.Max(0, Mathf.CeilToInt(seconds));
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        private static string F(float value) { return NetSkillServer.F(value); }
    }
}
