using System.IO;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using Emberdelve.Skills;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B2, client process, owner of a player object only (added by NetPlayerRig.OnNetworkSpawn): the producer of the skill key
    /// presses. It is the ONLY thing a client does about skills: it asks the server (NetPlayerRig.SkillIntentRpc, reliable and ordered)
    /// and shows what the server answers (NetOwnerSkillView). It never casts, spends or predicts anything: SkillCaster refuses to cast
    /// in a client-only process (SkillCaster.ClientRoleBlocks counts the attempts), so a press is executed exactly once, on the server.
    ///
    /// Sequence: ONE counter per connection, shared by both slots, starting at 1; it advances only when a request is actually sent.
    /// The channel is reliable and ordered, so the client never resends or reorders. Throttle: at most one request per
    /// <see cref="SkillNetLimits.ClientMinIntervalSeconds"/>, a press inside that gap is dropped on the client (counted), which keeps an
    /// honest key-masher under the server's gate budget and out of its reject ledger.
    ///
    /// Input sources, in this order: a scripted driver (-ember-net-script skill-cast, works headless: the same send path as a key press),
    /// the development hostile probes (skill-proof, skill-abuse; compiled out of release builds), the real EmberdelveInput of a windowed
    /// owner (Skill1Pressed / Skill2Pressed). A scripted client takes no real input (the windowed-probe footgun of the earlier proof).
    ///   [client] skill-intent sent seq=N slot=S
    ///   [client] script skill-cast|skill-proof|skill-abuse ...
    ///   [client] skill-sender final sent=.. throttled=.. lastSeq=.. clientCastBlocks=..
    /// </summary>
    public sealed class NetSkillSender : MonoBehaviour
    {
        private const float DefaultStartDelay = 1.5f;
        private const float StartFilePollSeconds = 0.25f;

        // skill-cast: slot 0, slot 0 again (refused: cooling down), slot 1.
        private static readonly float[] CastTimes = { 0f, 0.4f, 0.8f };
        private static readonly int[] CastSlots = { 0, 0, 1 };

        private NetPlayerRig _owner;
        private PlayerRig _rig;
        private NetLaunchArgs _args;
        private NetScriptSpec _spec;
        private bool _useRealInput;

        private uint _sequence;
        private float _lastSendAt = -100f;
        private int _sent;
        private int _throttled;

        private float _spawnedAt;
        private float _nextStartPoll;
        private bool _scriptStarted;
        private bool _scriptDone;
        private float _scriptStartedAt;
        private int _castStep;

        public uint LastSequence => _sequence;
        public int Sent => _sent;
        public int Throttled => _throttled;

        public void Configure(NetPlayerRig owner, NetLaunchArgs args)
        {
            _owner = owner;
            _rig = owner != null ? owner.Rig : null;
            _args = args;
            _spawnedAt = Time.unscaledTime;

            NetScriptSpec spec = args != null ? args.ScriptSpec : null;
            if (spec != null && spec.IsSkillScript)
            {
                _spec = spec;
#if !(DEVELOPMENT_BUILD || UNITY_EDITOR)
                if (NetScriptSpec.IsDevelopmentOnly(spec.Steps[0].Kind))
                {
                    Debug.Log(NetTuning.LogPrefix + "[client] script " + spec.Text + " unavailable (not a development build)");
                    _spec = null;
                }
#endif
            }

            // A client that was given ANY script is a scripted client: no real keyboard even in a window (NetInputSender uses the same rule).
            _useRealInput = (args == null || args.ScriptSpec == null) && !Application.isBatchMode && _rig != null && _rig.Input != null;

            Debug.Log(NetTuning.LogPrefix + "[client] skill sender started script=" + (_spec != null ? _spec.Text : "-") +
                      " realInput=" + _useRealInput + " minInterval=" + SkillNetLimits.ClientMinIntervalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        }

        public void LogFinal()
        {
            Debug.Log(NetTuning.LogPrefix + "[client] skill-sender final sent=" + _sent + " throttled=" + _throttled + " lastSeq=" + _sequence +
                      " clientCastBlocks=" + SkillCaster.ClientRoleBlocks);
        }

        private void Update()
        {
            if (_owner == null || !_owner.IsSpawned) return;

            if (_spec != null)
            {
                RunScript();
                return;
            }

            if (!_useRealInput || _rig == null || _rig.Input == null) return;
            var inventory = GetComponent<NetInventoryClient>();
            if (inventory != null && inventory.IsOpen) return;
            if (_rig.Input.Skill1Pressed) TryRequest(0);
            if (_rig.Input.Skill2Pressed) TryRequest(1);
        }

        /// <summary>One key press: throttled on the client, then one reliable request with the next sequence number. False when it was not sent.</summary>
        public bool TryRequest(int slot)
        {
            if (slot < 0 || slot >= SkillNetLimits.ActiveSlots || _owner == null || !_owner.IsSpawned) return false;

            float now = Time.unscaledTime;
            if (now - _lastSendAt < SkillNetLimits.ClientMinIntervalSeconds)
            {
                _throttled++;
                return false;
            }

            _lastSendAt = now;
            _sequence++;
            _sent++;
            _owner.SkillIntentRpc(slot, _sequence);
            Debug.Log(NetTuning.LogPrefix + "[client] skill-intent sent seq=" + _sequence + " slot=" + slot);
            return true;
        }

        // ---- scripted driver -------------------------------------------------------------------

        private bool StartConditionMet()
        {
            string file = _args != null ? _args.ScriptStartFile : "";
            if (string.IsNullOrEmpty(file)) return Time.unscaledTime - _spawnedAt >= DefaultStartDelay;

            float now = Time.unscaledTime;
            if (now < _nextStartPoll) return false;
            _nextStartPoll = now + StartFilePollSeconds;
            return File.Exists(file);
        }

        private void RunScript()
        {
            if (_scriptDone) return;

            if (!_scriptStarted)
            {
                if (!StartConditionMet()) return;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                // The hostile probes need somebody else's object to attack; wait until a view of one exists.
                if ((_spec.IsSkillProofOnly || _spec.IsSkillAbuseOnly) && NetPlayerRig.FindProbeVictim() == null) return;
#endif
                _scriptStarted = true;
                _scriptStartedAt = Time.unscaledTime;
                Debug.Log(NetTuning.LogPrefix + "[client] script " + _spec.Text + " start");
            }

            if (_spec.IsSkillCastOnly)
            {
                float elapsed = Time.unscaledTime - _scriptStartedAt;
                while (_castStep < CastTimes.Length && elapsed >= CastTimes[_castStep])
                {
                    TryRequest(CastSlots[_castStep]);
                    _castStep++;
                }

                if (_castStep >= CastTimes.Length)
                {
                    _scriptDone = true;
                    Debug.Log(NetTuning.LogPrefix + "[client] script skill-cast done sent=" + _sent + " throttled=" + _throttled);
                }

                return;
            }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _scriptDone = true;
            if (_spec.IsSkillProofOnly) RunProof();
            else if (_spec.IsSkillAbuseOnly) RunAbuse();
#else
            _scriptDone = true;
#endif
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        /// <summary>
        /// Development probe (the U3B1 sequence on the production channel): a forged request on another player's object, then our own
        /// slot 1 (accepted), a replay of it, a malformed slot, and a fresh request while that skill is cooling down.
        /// </summary>
        private void RunProof()
        {
            NetPlayerRig victim = NetPlayerRig.FindProbeVictim();
            int victimId = victim != null ? victim.ServerPlayerId.Value : 0;
            // Forge first: it must not consume the real owner's sequence or alter their kit.
            victim.SkillIntentRpc(1, 1);
            _owner.SkillIntentRpc(1, 1);
            _owner.SkillIntentRpc(1, 1); // replay
            _owner.SkillIntentRpc(9, 2); // invalid slot
            _owner.SkillIntentRpc(1, 2); // fresh, but the same skill is cooling down
            Debug.Log(NetTuning.LogPrefix + "[client] script skill-proof sent target=" + victimId + " forged=1 self=4");
        }

        private const int AbuseForged = 3;
        private const int AbuseFlood = 20;

        /// <summary>
        /// Development probe: everything a hostile client can do to the skill channel, in one burst, in this order: forged requests on
        /// another player's object (identity), one valid request, its replay, a malformed slot, then a flood of well formed requests.
        /// The server must count every reject on THIS connection, leave the victim untouched and disconnect this client once the excess
        /// reaches its threshold; whatever is sent after that is dropped.
        /// </summary>
        private void RunAbuse()
        {
            NetPlayerRig victim = NetPlayerRig.FindProbeVictim();
            int victimId = victim != null ? victim.ServerPlayerId.Value : 0;
            for (uint i = 1; i <= AbuseForged; i++) victim.SkillIntentRpc(0, i);

            _owner.SkillIntentRpc(0, 1);   // valid
            _owner.SkillIntentRpc(0, 1);   // replay
            _owner.SkillIntentRpc(9, 2);   // invalid slot
            for (uint seq = 2; seq < 2 + AbuseFlood; seq++) _owner.SkillIntentRpc(1, seq);
            Debug.Log(NetTuning.LogPrefix + "[client] script skill-abuse sent target=" + victimId + " forged=" + AbuseForged +
                      " valid=1 replay=1 invalid=1 flood=" + AbuseFlood);
        }
#endif
    }
}
