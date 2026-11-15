using System.Globalization;
using Emberdelve.Movement;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3A4b, client process, owner of a player object only (added by NetPlayerRig.OnNetworkSpawn). The owner's movement
    /// prediction and reconciliation:
    ///
    ///   - PredictFrame (called by NetInputSender once per network tick, for the frame it is about to send): steps the owner's own
    ///     PlayerMovement with that frame (as the server will see it, PredictionRules.ServerView) and the tick dt, then stores
    ///     (seq, input, resulting state) in the ring. The body therefore moves the moment the player presses a key.
    ///   - OnAck (called by NetPlayerRig.AckRpc, unreliable, every second server tick): compares the server's state after
    ///     LastProcessedSeq with the stored prediction for that sequence number. Error up to 0.10 m is ignored, up to 1.0 m is blended
    ///     into the body over about 100 ms, above 1.0 m the body is teleported to the server state and the frames the server has not
    ///     processed yet are replayed from there. The decisions are PredictionReconciler (pure, unit tested); this class only carries
    ///     them out on the CharacterController.
    ///   - The owner's NetworkTransform must not fight the prediction. Decision (recorded in Docs/Tasks/U3A4b.md): NGO's
    ///     AnticipatedNetworkTransform is NOT used (it writes the transform itself, which a CharacterController overrides, it has no
    ///     notion of an input sequence to replay, and swapping the component needs a prefab rebuild); a small custom loop is. The
    ///     plain NetworkTransform on the owner is switched off when prediction begins, and as a safety net every tick starts by
    ///     checking that the body is still where this class left it (a mismatch is counted as a "fight" and undone).
    ///
    /// Prediction starts when the server's resolved movement numbers (MoveParams, owner-read) have arrived. Until then the owner
    /// follows the server exactly as in 4a. Logs: "[client] reconcile acks=.. maxErr=.. meanErr=.. blends=.. snaps=.. replays=.. ..."
    /// periodically, at script step ends and at the end, and "[client] predict pos=(x,y,z) seq=N" (the owner's own position).
    /// </summary>
    public sealed class NetOwnerPrediction : MonoBehaviour
    {
        private const float StatsLogSeconds = 5f;
        private const float PosLogSeconds = 0.5f;
        private const float PosHeartbeatSeconds = 2f;
        private const float PosMoveEpsilon = 0.002f;
        private const float FightEpsilon = 0.001f;

        private NetPlayerRig _owner;
        private PlayerRig _rig;
        private PlayerMovement _movement;
        private PlayerLook _look;
        private CharacterController _controller;
        private NetworkTransform _networkTransform;

        private readonly PredictionReconciler _recon = new PredictionReconciler();
        private bool _predicting;
        private Vector3 _lastPos;
        private uint _lastPredictedSeq;
        private uint _lastAckSeq;
        private int _acksReceived;
        private int _discardedFloorAcks;
        private uint _lastDiscardedGeneration = uint.MaxValue;
        private uint _lastDiscardedCurrent = uint.MaxValue;

        private float _nextStatsLog;
        private int _statsLoggedAcks = -1;
        private float _nextPosLog;
        private float _lastPosLoggedAt;
        private bool _posLogged;
        private Vector3 _posLogged3;
        private bool _destroyedLogged;

        public bool IsPredicting => _predicting;
        public ReconcileStats Stats => _recon.Stats;

        public void Configure(NetPlayerRig owner)
        {
            _owner = owner;
            _rig = owner.Rig;
            _movement = _rig != null ? _rig.Movement : null;
            _look = _rig != null ? _rig.Look : null;
            _controller = owner.GetComponent<CharacterController>();
            _networkTransform = owner.GetComponent<NetworkTransform>();
            _nextStatsLog = Time.unscaledTime + StatsLogSeconds;
        }

        /// <summary>The position the rest of the game should treat as the owner's: the body plus the correction still owed.</summary>
        public Vector3 LogicalPosition
        {
            get
            {
                Vector3 p = transform.position;
                return new Vector3(p.x + _recon.Blend.RemainingX, p.y + _recon.Blend.RemainingY, p.z + _recon.Blend.RemainingZ);
            }
        }

        // ---- start -----------------------------------------------------------------------------------------------

        private bool TryBegin()
        {
            if (_owner == null || _movement == null || _controller == null) return false;

            MoveParamsData data = _owner.MoveParams.Value.ToData();
            if (!data.IsValid) return false;

            // The body must be a live CharacterController at the position the server spawned it at. The plain NetworkTransform is
            // switched off from here on (UNCERTAIN, see class remarks: the fight guard below is the safety net if it keeps writing).
            Vector3 spawnPosition = transform.position;
            if (_networkTransform != null) _networkTransform.enabled = false;
            _controller.enabled = true;
            if (!_movement.ApplyPredictionParams(in data)) return false;
            _movement.TeleportTo(spawnPosition);

            _lastPos = transform.position;
            _predicting = true;
            Debug.Log(NetTuning.LogPrefix + "[client] prediction started walk=" + data.Walk.ToString("F3", CultureInfo.InvariantCulture) +
                      " sprint=" + data.Sprint.ToString("F3", CultureInfo.InvariantCulture) +
                      " crouch=" + data.Crouch.ToString("F3", CultureInfo.InvariantCulture) +
                      " jump=" + data.JumpHeight.ToString("F3", CultureInfo.InvariantCulture) +
                      " pos=" + Pos(spawnPosition) + " ntDisabled=" + (_networkTransform != null && !_networkTransform.enabled));
            return true;
        }

        /// <summary>The server sent new resolved numbers (a speed penalty, gear, a status): the same step keeps running with them.</summary>
        public void OnParamsChanged(in MoveParamsData data)
        {
            if (_predicting) _movement.ApplyPredictionParams(in data);
        }

        public void ResetForFloorMovement()
        {
            _recon.ResetForTeleport();
            _lastPredictedSeq = _lastAckSeq = 0;
            _lastPos = transform.position;
            _predicting = false;
            _posLogged = false;
        }

        // ---- predict ---------------------------------------------------------------------------------------------

        /// <summary>One network tick, one frame: step the body with it and remember what happened.</summary>
        public void PredictFrame(in MoveInputFrame frame)
        {
            if (_rig == null || _rig.IsInFloorTransit) return;
            if (!_predicting && !TryBegin()) return;
            if (_movement == null) return;

            const float dt = NetTuning.TickSeconds;

            // Something else moved the body since this class left it (the plain transform sync): count it and undo it.
            Vector3 now = transform.position;
            if ((now - _lastPos).sqrMagnitude > FightEpsilon * FightEpsilon)
            {
                _recon.Stats.Fights++;
                _movement.PlaceAt(_lastPos);
            }

            // A blend owed to the body is paid in small shares before the step.
            if (_recon.Blend.Take(dt, out float sx, out float sy, out float sz)) _movement.NudgeBy(new Vector3(sx, sy, sz));

            MoveInputFrame seen = PredictionRules.ServerView(in frame);
            if (_look != null) _look.ApplyAimPose(seen.Yaw, seen.Pitch);
            _movement.Step(in seen, dt);
            _lastPos = transform.position;

            _movement.CapturePredictionState(out PredictedState state);
            state.X += _recon.Blend.RemainingX;
            state.Y += _recon.Blend.RemainingY;
            state.Z += _recon.Blend.RemainingZ;

            PredictionEntry entry = new PredictionEntry { Seq = seen.Seq, Frame = seen, State = state };
            _recon.Record(in entry);
            _lastPredictedSeq = seen.Seq;
        }

        // ---- reconcile -------------------------------------------------------------------------------------------

        public void OnAck(in MoveAckWire ack)
        {
            if (_rig == null) return;
            if (_rig.IsInFloorTransit || ack.Generation != _rig.FloorMovementGeneration)
            {
                _discardedFloorAcks++;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                if (_discardedFloorAcks <= 3 || _lastDiscardedGeneration != ack.Generation || _lastDiscardedCurrent != _rig.FloorMovementGeneration)
                    Debug.Log(NetTuning.LogPrefix + "[client] discarded movement ack generation=" + ack.Generation
                        + " current=" + _rig.FloorMovementGeneration);
                _lastDiscardedGeneration = ack.Generation; _lastDiscardedCurrent = _rig.FloorMovementGeneration;
#endif
                return;
            }
            _acksReceived++;
            if (!_predicting) return;

            PredictedState server = ack.ToState();
            ReconcileOutcome outcome = _recon.OnAck(ack.LastProcessedSeq, in server);
            if (outcome.Verdict == ReconcileVerdict.Snap) SnapAndReplay(in outcome);
            if (ack.LastProcessedSeq > _lastAckSeq) _lastAckSeq = ack.LastProcessedSeq;
        }

        private void SnapAndReplay(in ReconcileOutcome outcome)
        {
            const float dt = NetTuning.TickSeconds;
            PredictedState server = outcome.Server;

            // Back to the server's state: position first (TeleportTo clears the velocities), then the Step state the Ack carried.
            _movement.TeleportTo(new Vector3(server.X, server.Y, server.Z));
            _movement.RestorePredictionState(in server);

            // Everything left in the ring is a frame the server has not processed yet: run it again from the corrected state.
            int count = _recon.Ring.Count;
            for (int i = 0; i < count; i++)
            {
                PredictionEntry entry = _recon.Ring[i];
                if (_look != null) _look.ApplyAimPose(entry.Frame.Yaw, entry.Frame.Pitch);
                _movement.Step(in entry.Frame, dt);
                _movement.CapturePredictionState(out PredictedState state);
                entry.State = state;
                _recon.Ring.Set(i, in entry);
            }

            _lastPos = transform.position;
            Debug.Log(NetTuning.LogPrefix + "[client] reconcile SNAP error=" + outcome.Error.ToString("F3", CultureInfo.InvariantCulture) +
                      " seq=" + _lastAckSeq + " to=" + Pos(new Vector3(server.X, server.Y, server.Z)) + " replayed=" + count);
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private ReconcileStatsSnapshot _proofIntervalStart;
        private bool _proofIntervalArmed;

        /// <summary>Starts a proof interval without changing cumulative prediction statistics.</summary>
        public void BeginProofInterval()
        {
            _proofIntervalStart = _recon.Stats.Capture();
            _proofIntervalArmed = true;
            Debug.Log(NetTuning.LogPrefix + "[client] reconcile proof-baseline acks=" + _proofIntervalStart.Acks +
                      " sumError=" + _proofIntervalStart.SumError.ToString("R", CultureInfo.InvariantCulture) +
                      " snaps=" + _proofIntervalStart.Snaps + " replays=" + _proofIntervalStart.Replays +
                      " fights=" + _proofIntervalStart.Fights);
        }

        /// <summary>Logs exact deltas, refusing an empty interval or counters that went backwards.</summary>
        public void LogProofInterval(string tag)
        {
            ReconcileStatsSnapshot end = _recon.Stats.Capture();
            bool valid = _proofIntervalArmed && _proofIntervalStart.TryInterval(end, out _, out _, out _, out _, out _);
            double mean = 0d;
            int acks = 0, snaps = 0, replays = 0, fights = 0;
            if (valid) _proofIntervalStart.TryInterval(end, out mean, out acks, out snaps, out replays, out fights);
            Debug.Log(NetTuning.LogPrefix + "[client] reconcile proof-interval valid=" + (valid ? "true" : "false") +
                      " acks=" + acks + " meanErr=" + mean.ToString("R", CultureInfo.InvariantCulture) +
                      " snaps=" + snaps + " replays=" + replays + " fights=" + fights + " at=" + tag);
        }
#endif

        // ---- logging ---------------------------------------------------------------------------------------------

        /// <summary>Writes the reconcile statistics line now (tag: what the moment is, e.g. "step:jump", "script-end", "final").</summary>
        public void LogStats(string tag)
        {
            _statsLoggedAcks = _recon.Stats.Acks;
            Debug.Log(NetTuning.LogPrefix + "[client] " + _recon.Stats.ToLogLine(tag) + " predicting=" + _predicting +
                      " ackMsgs=" + _acksReceived + " lastSeq=" + _lastPredictedSeq + " lastAck=" + _lastAckSeq);
        }

        private void Update()
        {
            if (_owner == null) return;
            float now = Time.unscaledTime;

            if (now >= _nextStatsLog)
            {
                _nextStatsLog = now + StatsLogSeconds;
                if (_recon.Stats.Acks != _statsLoggedAcks) LogStats("periodic");
            }

            if (_predicting && now >= _nextPosLog)
            {
                _nextPosLog = now + PosLogSeconds;
                Vector3 pos = LogicalPosition;
                bool moved = !_posLogged || (pos - _posLogged3).sqrMagnitude > PosMoveEpsilon * PosMoveEpsilon;
                bool heartbeat = _posLogged && now - _lastPosLoggedAt >= PosHeartbeatSeconds;
                if (moved || heartbeat)
                {
                    _posLogged = true;
                    _posLogged3 = pos;
                    _lastPosLoggedAt = now;
                    Debug.Log(NetTuning.LogPrefix + "[client] predict pos=" + Pos(pos) + " seq=" + _lastPredictedSeq + " ack=" + _lastAckSeq);
                }
            }
        }

        /// <summary>The last statistics line (once). NetPlayerRig calls it at despawn; OnDestroy covers every other way out.</summary>
        public void LogFinal()
        {
            if (_destroyedLogged || _owner == null) return;
            _destroyedLogged = true;
            if (_recon.Stats.Acks > 0 || _predicting) LogStats("final");
        }

        private void OnDestroy()
        {
            LogFinal();
        }

        private static string Pos(Vector3 v)
        {
            return "(" + v.x.ToString("F3", CultureInfo.InvariantCulture) + "," + v.y.ToString("F3", CultureInfo.InvariantCulture) +
                   "," + v.z.ToString("F3", CultureInfo.InvariantCulture) + ")";
        }
    }
}
