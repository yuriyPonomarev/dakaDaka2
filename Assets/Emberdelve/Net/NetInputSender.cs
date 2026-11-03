using System.Globalization;
using System.IO;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3A4a, client process, owner of a player object only (added by NetPlayerRig.OnNetworkSpawn). Produces the movement stream:
    ///
    ///   render frame (Update): sample the input source into a MoveInputAggregator. The source is, in this order: the scripted driver
    ///   (-ember-net-script, the same send path without the Input System, so it works headless), the real EmberdelveInput for a
    ///   windowed owner (move axes, sprint, jump / crouch edges, mouse look integrated into a view yaw / pitch), or an idle stream
    ///   (no input, the spawn yaw).
    ///   network tick (NetworkTickSystem.Tick): ONE aggregated frame, sent with the previous two as one unreliable RPC.
    ///
    /// 4a had no local simulation. U3A4b: for every honest frame it flushes, this sender first hands the frame to the sibling
    /// NetOwnerPrediction (which steps the owner's own PlayerMovement with it), then sends it. The extra frames of the flood steps
    /// are NOT predicted (a hostile stream by definition; the server would not move more for them either), nor are the hostile
    /// frames below. The hostile script steps (flood / replay / NaN / big move / sequence jump / forged owner) only add
    /// extra, deliberately bad frames on top of the idle honest stream; they exist so the server's counters can be proven, and
    /// the whole class is a test driver outside DEVELOPMENT_BUILD / the Editor (the hostile paths are compiled out there).
    /// </summary>
    public sealed class NetInputSender : MonoBehaviour
    {
        private const float DefaultStartDelay = 1.5f;
        private const float StartFilePollSeconds = 0.25f;
        private const float GamepadTurnSpeed = 70f;
        private const float LookScale = 1.2f * 0.4f;
        private const int FloodBatchesPerTick = 10;
        private const uint ReplayDistance = 30u;
        private const uint SeqJumpDistance = 100000u;

        private NetPlayerRig _owner;
        private NetOwnerPrediction _prediction;
        private PlayerRig _rig;
        private NetworkManager _nm;
        private NetworkTickSystem _ticks;
        private NetLaunchArgs _args;

        private readonly MoveInputAggregator _aggregator = new MoveInputAggregator();
        private readonly MoveInputFrame[] _batch = new MoveInputFrame[MoveInputAggregator.BatchSize];
        private readonly MoveInputFrame[] _hostile = new MoveInputFrame[MoveInputAggregator.BatchSize];

        private NetScriptSpec _spec;
        private NetScriptTimeline _timeline;
        private bool _scriptStarted;
        private float _scriptStartedAt;
        private int _scriptCompleted;
        private bool _scriptLoggedAll;
        private float _nextStartPoll;
        private NetScriptKind _activeKind;
        private bool _activeWantsForward;

        private bool _useRealInput;
        private bool _viewInitialized;
        private float _yaw;
        private float _pitch;
        private float _spawnedAt;
        private long _sentBatches;

        public void Configure(NetPlayerRig owner, NetLaunchArgs args)
        {
            _owner = owner;
            _prediction = owner.GetComponent<NetOwnerPrediction>();
            _rig = owner.Rig;
            _args = args;
            _nm = owner.NetworkManager;
            _spawnedAt = Time.unscaledTime;

            NetScriptSpec spec = args != null ? args.ScriptSpec : null;
            _spec = spec != null && !spec.IsProbeOnly ? spec : null; // probe scripts (forge-owner, skill-*) are not movement scripts
            // U3B2: a client that was given ANY script is a scripted client and takes no real input, even in a window (a probe script
            // used to leave the keyboard live because _spec was nulled above).
            _useRealInput = spec == null && !Application.isBatchMode && _rig != null && _rig.Input != null;
            if (_useRealInput) _rig.SetInputEnabled(true);

            if (_nm != null && _nm.NetworkTickSystem != null)
            {
                _ticks = _nm.NetworkTickSystem;
                _ticks.Tick += OnNetworkTick;
            }

            Debug.Log(NetTuning.LogPrefix + "[client] movement stream started script=" + (_spec != null ? _spec.Text : "-") +
                      " realInput=" + _useRealInput + " tickRate=" + NetTuning.TickRate);
        }

        private void OnDestroy()
        {
            if (_ticks != null) _ticks.Tick -= OnNetworkTick;
            _ticks = null;
            if (_useRealInput && _rig != null) _rig.SetInputEnabled(false);
        }

        // ---- render frame: sample ---------------------------------------------------------------

        private void Update()
        {
            if (_owner == null || !_owner.IsSpawned) return;
            if (!_viewInitialized) InitializeView();

            if (_spec != null) SampleScript();
            else if (_useRealInput) SampleRealInput();
            else _aggregator.Sample(0f, 0f, false, false, false, _yaw, _pitch);
        }

        private void InitializeView()
        {
            _viewInitialized = true;
            _yaw = _args != null && _args.HasScriptYaw ? _args.ScriptYaw : MoveInputFrame.WrapDegrees(_owner.transform.eulerAngles.y);
            _pitch = 0f;
            if (_spec != null) _timeline = new NetScriptTimeline(_spec, _yaw);
        }

        private void SampleRealInput()
        {
            Emberdelve.InputLayer.EmberdelveInput input = _rig.Input;
            Vector2 look = input.Look;
            if (look.sqrMagnitude > 0.0001f)
            {
                float multiplier = look.sqrMagnitude > 1f ? 1f : Time.deltaTime * GamepadTurnSpeed;
                _yaw = MoveInputFrame.WrapDegrees(_yaw + look.x * LookScale * multiplier);
                _pitch = MoveInputFrame.ClampPitch(_pitch - look.y * LookScale * multiplier);
                // U3A4b: the owner's own view is local (yaw and pitch), not a round trip; movement and the server use the same numbers
                // from the frame. Before prediction exists (4a behaviour) only the pitch is applied and the body yaw follows the server.
                if (_rig.Look != null)
                {
                    if (_prediction != null && _prediction.IsPredicting) _rig.Look.ApplyAimPose(_yaw, _pitch);
                    else _rig.Look.ApplyPitch(_pitch);
                }
            }

            Vector2 move = input.Move;
            _aggregator.Sample(move.x, move.y, input.SprintHeld, input.JumpPressed, input.CrouchPressed, _yaw, _pitch);
        }

        private void SampleScript()
        {
            if (_timeline == null) return;

            if (!_scriptStarted)
            {
                if (StartConditionMet())
                {
                    _scriptStarted = true;
                    _scriptStartedAt = Time.unscaledTime;
                    Debug.Log(NetTuning.LogPrefix + "[client] script " + _spec.Text + " start pos=" + Pos(_owner.transform.position));
                }
                else
                {
                    _yaw = _timeline.YawBeforeStart;
                    _activeKind = NetScriptKind.None;
                    _aggregator.Sample(0f, 0f, false, false, false, _yaw, 0f);
                    return;
                }
            }

            float elapsed = Time.unscaledTime - _scriptStartedAt;
            NetScriptIntent intent = _timeline.Evaluate(elapsed);
            _yaw = intent.Yaw;
            _activeKind = intent.ActiveKind;
            _activeWantsForward = intent.ActiveStep >= 0 &&
                                  (intent.ActiveKind == NetScriptKind.ReplaySeq ||
                                   intent.ActiveKind == NetScriptKind.SeqJump || intent.ActiveKind == NetScriptKind.ForgeMove);
            if (intent.ActiveStep < 0) _activeKind = NetScriptKind.None;
            _aggregator.Sample(intent.MoveX, intent.MoveY, intent.Sprint, intent.Jump, false, intent.Yaw, 0f);

            int completed = _timeline.CompletedSteps(elapsed);
            while (_scriptCompleted < completed)
            {
                NetScriptStep step = _spec.Steps[_scriptCompleted];
                _scriptCompleted++;
                Debug.Log(NetTuning.LogPrefix + "[client] script " + step.Name + " done pos=" + Pos(_owner.transform.position));
                if (_prediction != null) _prediction.LogStats("step:" + step.Name);
            }

            if (intent.Done && !_scriptLoggedAll)
            {
                _scriptLoggedAll = true;
                Debug.Log(NetTuning.LogPrefix + "[client] script chain done steps=" + _spec.Steps.Count + " pos=" + Pos(_owner.transform.position));
                if (_prediction != null) _prediction.LogStats("script-end");
            }
        }

        private bool StartConditionMet()
        {
            string file = _args != null ? _args.ScriptStartFile : "";
            if (string.IsNullOrEmpty(file)) return Time.unscaledTime - _spawnedAt >= DefaultStartDelay;

            float now = Time.unscaledTime;
            if (now < _nextStartPoll) return false;
            _nextStartPoll = now + StartFilePollSeconds;
            return File.Exists(file);
        }

        // ---- network tick: send ----------------------------------------------------------------

        private void OnNetworkTick()
        {
            if (_owner == null || !_owner.IsSpawned || !_viewInitialized) return;

            int batches = 1;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (_activeKind == NetScriptKind.FloodInput || _activeKind == NetScriptKind.FloodRun) batches = FloodBatchesPerTick;
#endif
            for (int i = 0; i < batches; i++)
            {
                MoveInputFrame flushed = _aggregator.Flush();
                // Only the first (honest) frame of a tick is predicted; flood extras are not.
                if (i == 0 && _prediction != null) _prediction.PredictFrame(in flushed);
                int count = _aggregator.FillBatch(_batch);
                _owner.SubmitMoveRpc(MoveBatchWire.From(_batch, count));
                _sentBatches++;
            }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            SendHostile();
#endif
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        /// <summary>Development / proof only: the extra, deliberately bad frames of the hostile script steps.</summary>
        private void SendHostile()
        {
            uint last = _aggregator.LastSeq;
            float forward = _activeWantsForward ? 1f : 0f;

            switch (_activeKind)
            {
                case NetScriptKind.ReplaySeq:
                    if (last > ReplayDistance + MoveInputAggregator.BatchSize)
                    {
                        for (int i = 0; i < MoveInputAggregator.BatchSize; i++)
                            _hostile[i] = Frame(last - ReplayDistance + (uint)i, 0f, 1f, _yaw, 0f, MoveInputFrame.FlagSprint);
                        _owner.SubmitMoveRpc(MoveBatchWire.From(_hostile, MoveInputAggregator.BatchSize));
                    }

                    break;

                case NetScriptKind.NanInput:
                    // Alternate the poison: NaN axes, then an infinite yaw with a NaN pitch.
                    if ((_sentBatches & 1) == 0)
                        _hostile[0] = Frame(last + 1u, float.NaN, float.NaN, _yaw, 0f, 0);
                    else
                        _hostile[0] = Frame(last + 1u, 0f, 1f, float.PositiveInfinity, float.NaN, 0);
                    _owner.SubmitMoveRpc(MoveBatchWire.From(_hostile, 1));
                    break;

                case NetScriptKind.BigMove:
                    _hostile[0] = Frame(last + 1u, 50f, 50f, _yaw, 0f, 0);
                    _owner.SubmitMoveRpc(MoveBatchWire.From(_hostile, 1));
                    break;

                case NetScriptKind.SeqJump:
                    _hostile[0] = Frame(last + SeqJumpDistance, 0f, forward, _yaw, 0f, 0);
                    _owner.SubmitMoveRpc(MoveBatchWire.From(_hostile, 1));
                    break;

                case NetScriptKind.ForgeMove:
                    {
                        NetPlayerRig victim = FindVictim();
                        if (victim != null)
                        {
                            _hostile[0] = Frame(last + 1u, 0f, forward, _yaw, 0f, MoveInputFrame.FlagSprint);
                            victim.ForgeMoveTestRpc(MoveBatchWire.From(_hostile, 1));
                        }
                    }

                    break;
            }
        }

        private static MoveInputFrame Frame(uint seq, float moveX, float moveY, float yaw, float pitch, byte flags)
        {
            return new MoveInputFrame { Seq = seq, MoveX = moveX, MoveY = moveY, Yaw = yaw, Pitch = pitch, Flags = flags };
        }

        /// <summary>Another player's object: the shared probe rule (NetPlayerRig.FindProbeVictim, the same one NetClientProbe and NetSkillSender use).</summary>
        private static NetPlayerRig FindVictim()
        {
            return NetPlayerRig.FindProbeVictim();
        }
#endif

        private static string Pos(Vector3 v)
        {
            return "(" + v.x.ToString("F3", CultureInfo.InvariantCulture) + "," + v.y.ToString("F3", CultureInfo.InvariantCulture) +
                   "," + v.z.ToString("F3", CultureInfo.InvariantCulture) + ")";
        }
    }
}
