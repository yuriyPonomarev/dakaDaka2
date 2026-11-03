using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Emberdelve.Movement;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3A4a, server only (owned by NetServer): the authoritative movement simulation.
    ///
    ///  - Receive: the entry point of both movement RPCs. The sender is resolved ONLY from the connection that carried the message
    ///    (RpcParams.Receive.SenderClientId, passed in by the RPC) through the NetIdentityTable; a frame for an object that
    ///    connection does not own is a forged-owner movement attempt: rejected and counted, nothing else happens. Frames from an
    ///    unknown connection (before the spawn finished, after the despawn) are ignored and counted globally.
    ///  - Every frame goes through the player's InputSanitizer and then into the player's ServerMoveInputBuffer.
    ///  - On every NGO network tick (NetworkManager.NetworkTickSystem.Tick, not Update) each registered player gets exactly ONE
    ///    step: one frame from the buffer (or the repeat / neutral rule), the aim pose applied (yaw to the body, pitch to the look
    ///    pivot, clamped), PlayerMovement.Step with dt = 1 / TickRate. Position, collision and speed are therefore the server's.
    ///
    /// Logging (all lines start with the server tag from NetBootstrap):
    ///   move id=N pos=(x,y,z) acc=.. rep=.. flood=.. inv=.. tick=.. yaw=.. clamp=.. qdrop=.. forge=.. vmax=.. ymax=.. air=.. depth=..
    ///        written when the player moved or a counter changed (at most every 15 ticks) and at least every 120 ticks; a last line
    ///        with final=1 plus the per-reason counters is written when the player is removed;
    ///   REJECT kind count=n id=N     (kinds: replay flood nan big-move seq-jump seq-zero flags bad-batch forge-move; rate limited:
    ///        counts 1..3, powers of two, every 100th);
    ///   seg id=N ... one line per finished stretch of horizontal movement (distance, mean / peak speed, from / to);
    ///   air id=N ... one line per jump / fall of at least 4 airborne ticks.
    ///
    /// U3A4b adds the way back to the owner: after the step of a QUEUED frame (a real client frame, not a repeat or a neutral fill)
    /// the state reached is remembered with that frame's sequence number, and every second tick the newest one goes to the owner as
    /// an unreliable Ack (NetPlayerRig.AckRpc). The state is the one right after that frame, not the one after later repeated
    /// steps, so the owner compares like with like. The server also publishes the movement numbers it resolved for the player
    /// (NetPlayerRig.MoveParams, owner-read; re-checked every 15 ticks) so the owner predicts with the same speeds. Extra log lines:
    ///   params id=N walk=.. sprint=.. crouch=.. stand=.. crouchH=.. jump=.. mask=..      (first publish and every change)
    ///   dev-shove applied id=N slot=.. from=(..) to=(..) meters=..     (development builds: -ember-net-dev-shove, the gate's forced correction)
    /// and the final line also carries acks=N (Acks sent) and shoves=N.
    /// </summary>
    internal sealed class NetMovementServer
    {
        private const float SegStartSpeed = 0.3f;
        private const float SegStopSpeed = 0.1f;
        private const int SegStopTicks = 6;
        private const int AirMinTicks = 4;
        private const int LogEveryTicks = 15;
        private const int LogIdleTicks = 120;
        private const float LogMoveEpsilon = 0.002f;

        private sealed class MoveState
        {
            public int PlayerId;
            public ulong ClientId;
            public string Slot;
            public PlayerRig Rig;
            public NetworkObject Object;
            public readonly InputSanitizer Sanitizer = new InputSanitizer();
            public readonly ServerMoveInputBuffer Buffer = new ServerMoveInputBuffer();

            public int Forge;
            public int BadBatch;

            public NetPlayerRig NetRig;
            public bool AckDirty;
            public uint AckSeq;
            public PredictedState AckState;
            public int AcksSent;
            public int Shoves;
            /// <summary>Server tick of the last real client frame (set at registration so a fresh player is not "quiet").</summary>
            public long LastFrameTick;
            public int AcksSuppressed;
            public int ParamsSuppressed;
            public long NextQuietLogTick;
            public bool ParamsPublished;
            public MoveParamsData LastParams;

            public Vector3 LastPos;
            public float VMax;
            public float VMaxSinceLog;
            public float YMaxSinceLog = float.NegativeInfinity;
            public float RestY;
            public int AirTicksTotal;
            public int AirTicks;
            public float AirYMax;
            public float AirRest;

            public bool Moving;
            public long SegStartTick;
            public Vector3 SegFrom;
            public Vector3 SegTo;
            public float SegDist;
            public float SegPeak;
            public int SegTicks;
            public int StillTicks;

            public long NextLogTick;
            public long LastLogTick = -1;
            public Vector3 LoggedPos;
            public long LoggedSignature = -1;
        }

        public static NetMovementServer Current { get; private set; }

        private readonly NetworkManager _nm;
        private readonly NetIdentityTable _table;
        private readonly Action<string> _log;
        private readonly Action<string> _logError;
        private readonly Dictionary<int, MoveState> _states = new Dictionary<int, MoveState>();
        private readonly List<MoveState> _order = new List<MoveState>();

        private const int ParamsCheckTicks = 15;

        /// <summary>
        /// U3A4b fix: a player that has sent no real input frame for this many ticks (1 s) is treated as gone for SENDING: no Ack, no
        /// params write. A hard-killed client is still "connected" until the transport timeout (4 s in the gates, 30 s by default); nothing
        /// useful can be delivered to it, so the server does not keep feeding a dead UDP endpoint.
        /// </summary>
        private const int QuietSendTicks = 30;
        private const int QuietLogTicks = 90;
        private const int ShovePollTicks = 10;

        private bool _subscribed;
        private long _tick;
        private float _lastTickRealtime = -1f;
        private float _maxGapSinceQuietLog;
        private int _noPlayer;
        private bool _shoveDone;

        public NetMovementServer(NetworkManager nm, NetIdentityTable table, Action<string> log, Action<string> logError)
        {
            _nm = nm;
            _table = table;
            _log = log;
            _logError = logError;
            Current = this;
        }

        public int Registered => _states.Count;
        public long TickCount => _tick;

        /// <summary>Called every frame by NetServer: attaches to the network tick once the transport is listening.</summary>
        public void EnsureSubscribed()
        {
            if (_subscribed || _nm == null || !_nm.IsListening) return;
            NetworkTickSystem ticks = _nm.NetworkTickSystem;
            if (ticks == null) return;

            ticks.Tick += OnNetworkTick;
            _subscribed = true;
            _log("movement simulation attached tickRate=" + NetTuning.TickRate + " dt=" + NetTuning.TickSeconds.ToString("F4", CultureInfo.InvariantCulture));
        }

        public void Dispose()
        {
            if (_subscribed && _nm != null)
            {
                NetworkTickSystem ticks = _nm.NetworkTickSystem;
                if (ticks != null) ticks.Tick -= OnNetworkTick;
            }

            _subscribed = false;
            _states.Clear();
            _order.Clear();
            if (Current == this) Current = null;
        }

        // ---- registration ----------------------------------------------------------------------

        /// <summary>A player was admitted and spawned. The host's own presenting player is not simulated here (it moves with its own Update).</summary>
        public void Register(NetIdentity row)
        {
            if (row == null || row.Rig == null || row.Object == null) return;
            if (row.ClientId == NetworkManager.ServerClientId) return;
            PlayerMovement movement = row.Rig.Movement;
            if (movement == null) { _logError("movement: player " + row.PlayerId + " has no PlayerMovement"); return; }
            if (_states.ContainsKey(row.PlayerId)) return;

            MoveState state = new MoveState
            {
                PlayerId = row.PlayerId,
                ClientId = row.ClientId,
                Slot = row.Slot,
                Rig = row.Rig,
                Object = row.Object,
                LastPos = row.Rig.transform.position,
                RestY = row.Rig.transform.position.y,
                LastFrameTick = _tick
            };
            state.LoggedPos = state.LastPos;
            state.NextLogTick = _tick;
            state.NetRig = row.Object.GetComponent<NetPlayerRig>();
            _states[row.PlayerId] = state;
            _order.Add(state);
            PublishParams(state);

            _log("movement registered id=" + row.PlayerId + " client=" + row.ClientId +
                 " walk=" + F(movement.ResolveGroundSpeed(false, false)) +
                 " sprint=" + F(movement.ResolveGroundSpeed(true, false)) +
                 " crouch=" + F(movement.ResolveGroundSpeed(false, true)) +
                 " jump=" + F(row.Rig.Class != null ? row.Rig.Class.jumpHeight : 0f) +
                 " pos=" + P(state.LastPos) + " tick=" + _tick);
        }

        /// <summary>
        /// U3B2: server ticks since this player's last real input frame, i.e. the age of the aim pose a skill cast resolves with (the rig's
        /// yaw / pitch are the last sanitized frame's). False for a player that is not simulated here (the host's own, or already removed).
        /// </summary>
        public bool TryGetInputAge(int playerId, out long ticks)
        {
            ticks = -1;
            if (!_states.TryGetValue(playerId, out MoveState state)) return false;
            ticks = _tick - state.LastFrameTick;
            return true;
        }

        public void Unregister(int playerId, string reason)
        {
            if (!_states.TryGetValue(playerId, out MoveState state)) return;

            _states.Remove(playerId);
            _order.Remove(state);
            if (state.Moving) EndSegment(state, true);
            WriteMoveLine(state, true, reason);
        }

        // ---- receive ---------------------------------------------------------------------------

        /// <summary>
        /// A movement batch arrived. <paramref name="target"/> is the player object the RPC was invoked on; <paramref name="senderClientId"/>
        /// is RpcParams.Receive.SenderClientId, the connection that actually delivered it.
        /// </summary>
        public void Receive(NetPlayerRig target, ulong senderClientId, in MoveBatchWire batch)
        {
            if (!_table.TryGetByClient(senderClientId, out NetIdentity sender) || !_states.TryGetValue(sender.PlayerId, out MoveState state))
            {
                _noPlayer++;
                if (IsLoggable(_noPlayer)) _log("REJECT no-player count=" + _noPlayer + " sender=" + senderClientId);
                return;
            }

            NetworkObject targetObject = target != null ? target.NetworkObject : null;
            if (targetObject == null || !ReferenceEquals(targetObject, state.Object))
            {
                state.Forge++;
                if (IsLoggable(state.Forge))
                {
                    int targetId = target != null ? target.ServerPlayerId.Value : 0;
                    _log("REJECT forge-move count=" + state.Forge + " id=" + state.PlayerId + " target=" + targetId +
                         " targetOwner=" + (targetObject != null ? targetObject.OwnerClientId : 0UL) + " sender=" + senderClientId);
                }

                return;
            }

            int count = batch.Count;
            if (count < 1 || count > MoveBatchWire.MaxFrames)
            {
                state.BadBatch++;
                if (IsLoggable(state.BadBatch)) _log("REJECT bad-batch count=" + state.BadBatch + " id=" + state.PlayerId);
                return;
            }

            double now = _tick / (double)NetTuning.TickRate;
            for (int i = 0; i < count; i++)
            {
                MoveInputFrame frame = batch.Get(i).ToFrame();
                bool primary = i == count - 1;
                SanitizeVerdict verdict = state.Sanitizer.Submit(in frame, primary, now, out MoveInputFrame clean);
                switch (verdict)
                {
                    case SanitizeVerdict.Accepted:
                        state.Buffer.Push(in clean);
                        break;
                    case SanitizeVerdict.DroppedReplay:
                        LogReject("replay", state.Sanitizer.DroppedReplay, state);
                        break;
                    case SanitizeVerdict.DroppedFlood:
                        LogReject("flood", state.Sanitizer.DroppedFlood, state);
                        break;
                    case SanitizeVerdict.DroppedInvalid:
                        LogInvalid(state);
                        break;
                }
            }
        }

        private void LogInvalid(MoveState state)
        {
            InputSanitizer s = state.Sanitizer;
            switch (s.LastInvalidReason)
            {
                case InvalidReason.NonFinite: LogReject("nan", s.InvalidNonFinite, state); break;
                case InvalidReason.MoveMagnitude: LogReject("big-move", s.InvalidMagnitude, state); break;
                case InvalidReason.SeqWindow: LogReject("seq-jump", s.InvalidSeqWindow, state); break;
                case InvalidReason.SeqZero: LogReject("seq-zero", s.InvalidSeqZero, state); break;
                case InvalidReason.Flags: LogReject("flags", s.InvalidFlags, state); break;
            }
        }

        private void LogReject(string kind, int count, MoveState state)
        {
            if (IsLoggable(count)) _log("REJECT " + kind + " count=" + count + " id=" + state.PlayerId);
        }

        private static bool IsLoggable(int count)
        {
            return count <= 3 || (count & (count - 1)) == 0 || count % 100 == 0;
        }

        // ---- the tick --------------------------------------------------------------------------

        private void OnNetworkTick()
        {
            _tick++;
            float realtime = Time.realtimeSinceStartup;
            if (_lastTickRealtime >= 0f && realtime - _lastTickRealtime > _maxGapSinceQuietLog) _maxGapSinceQuietLog = realtime - _lastTickRealtime;
            _lastTickRealtime = realtime;
            const float dt = NetTuning.TickSeconds;
            bool ackTick = _tick % ReconcileRules.AckEveryTicks == 0;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            MaybeShove();
#endif

            for (int i = _order.Count - 1; i >= 0; i--)
            {
                MoveState state = _order[i];
                PlayerRig rig = state.Rig;
                if (rig == null || state.Object == null || !state.Object.IsSpawned)
                {
                    // Despawned or destroyed under us: stop simulating it (NetServer.RemoveActor normally got there first).
                    _order.RemoveAt(i);
                    _states.Remove(state.PlayerId);
                    continue;
                }

                StepPlayer(state, rig, dt);
                if (ackTick) SendAck(state);
                if ((_tick + state.PlayerId) % ParamsCheckTicks == 0) PublishParams(state);
            }

            // Oldest registration first for the log lines (the loop above runs backwards so removal is safe).
            for (int i = 0; i < _order.Count; i++)
            {
                MaybeLog(_order[i]);
                MaybeLogQuiet(_order[i]);
            }
        }

        private void StepPlayer(MoveState state, PlayerRig rig, float dt)
        {
            PlayerMovement movement = rig.Movement;
            PlayerLook look = rig.Look;
            Transform body = rig.transform;
            if (movement == null) return;

            float fallbackYaw = MoveInputFrame.WrapDegrees(body.eulerAngles.y);
            float fallbackPitch = look != null ? look.Pitch : 0f;
            MoveFrameSource source = state.Buffer.Next(fallbackYaw, fallbackPitch, out MoveInputFrame frame);

            if (look != null) look.ApplyAimPose(frame.Yaw, frame.Pitch);
            movement.Step(in frame, dt);

            // U3A4b: remember the state right after a real client frame, under that frame's sequence number (the Ack's content).
            if (source == MoveFrameSource.Queued)
            {
                state.AckSeq = frame.Seq;
                movement.CapturePredictionState(out state.AckState);
                state.AckDirty = true;
                state.LastFrameTick = _tick;
            }

            Vector3 pos = body.position;
            float dx = pos.x - state.LastPos.x;
            float dz = pos.z - state.LastPos.z;
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            float speed = distance / dt;
            float dy = pos.y - state.LastPos.y;

            if (speed > state.VMax) state.VMax = speed;
            if (speed > state.VMaxSinceLog) state.VMaxSinceLog = speed;
            if (pos.y > state.YMaxSinceLog) state.YMaxSinceLog = pos.y;

            TrackSegment(state, pos, distance, speed);
            TrackAir(state, movement, pos, dy);

            state.LastPos = pos;
        }

        // ---- U3A4b: owner replication ----------------------------------------------------------------

        /// <summary>
        /// True when something may be sent to this player's owner: the object is spawned, its owner is still in the NetworkManager's
        /// connected set, and the owner has delivered a real input frame within <see cref="QuietSendTicks"/>.
        /// </summary>
        private bool CanSendTo(MoveState state)
        {
            NetPlayerRig rig = state.NetRig;
            if (rig == null || !rig.IsSpawned || _nm == null || !_nm.IsListening) return false;
            if (_tick - state.LastFrameTick > QuietSendTicks) return false;

            ulong owner = rig.OwnerClientId;
            IReadOnlyList<ulong> ids = _nm.ConnectedClientsIds;
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] == owner) return true;
            return false;
        }

        private void SendAck(MoveState state)
        {
            if (!state.AckDirty || state.NetRig == null || !state.NetRig.IsSpawned) return;

            if (!CanSendTo(state))
            {
                state.AckDirty = false;
                state.AcksSuppressed++;
                return;
            }

            state.AckDirty = false;
            state.AcksSent++;
            state.NetRig.AckRpc(MoveAckWire.From(state.AckSeq, (uint)_tick, in state.AckState));
        }

        /// <summary>Resolves the player's movement numbers and writes them to the owner-read variable when they changed.</summary>
        private void PublishParams(MoveState state)
        {
            PlayerMovement movement = state.Rig != null ? state.Rig.Movement : null;
            var cls = state.Rig != null ? state.Rig.Class : null;
            if (movement == null || cls == null || state.NetRig == null || !state.NetRig.IsSpawned) return;

            MoveParamsData data = new MoveParamsData
            {
                Walk = movement.ResolveGroundSpeed(false, false),
                Sprint = movement.ResolveGroundSpeed(true, false),
                Crouch = movement.ResolveGroundSpeed(false, true),
                StandHeight = cls.standHeight,
                CrouchHeight = cls.crouchHeight,
                JumpHeight = cls.jumpHeight,
                GroundMask = movement.GroundMask.value
            };

            if (state.ParamsPublished && data.Equals(state.LastParams)) return;

            // Not published, not remembered as published: it goes out on the first check after the owner is heard from again.
            if (state.ParamsPublished && !CanSendTo(state))
            {
                state.ParamsSuppressed++;
                return;
            }

            state.ParamsPublished = true;
            state.LastParams = data;
            state.NetRig.MoveParams.Value = MoveParamsWire.From(in data);
            _log("params id=" + state.PlayerId + " walk=" + F(data.Walk) + " sprint=" + F(data.Sprint) + " crouch=" + F(data.Crouch) +
                 " stand=" + F(data.StandHeight) + " crouchH=" + F(data.CrouchHeight) + " jump=" + F(data.JumpHeight) +
                 " mask=" + data.GroundMask + " tick=" + _tick);
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        /// <summary>
        /// Development only (-ember-net-dev-shove SLOT:YAW[:M] with -ember-net-dev-shove-file PATH): once, when the file exists, the
        /// named player is teleported M meters along YAW. Done between two steps, and the speed tracker is told, so the jump is not
        /// counted as speed. The owner's prediction cannot know about it: the next Acks show a large error and the owner snaps.
        /// </summary>
        private void MaybeShove()
        {
            if (_shoveDone || _tick % ShovePollTicks != 0) return;
            NetLaunchArgs args = NetBootstrap.LaunchArgs;
            if (args == null || args.DevShove == null || string.IsNullOrEmpty(args.DevShoveFile) || !File.Exists(args.DevShoveFile)) return;

            NetDevShove shove = args.DevShove;
            MoveState target = null;
            for (int i = 0; i < _order.Count; i++)
            {
                if (string.Equals(_order[i].Slot, shove.Slot, StringComparison.OrdinalIgnoreCase)) { target = _order[i]; break; }
            }

            if (target == null || target.Rig == null || target.Rig.Movement == null) return; // not here (yet): keep polling

            _shoveDone = true;
            Vector3 from = target.Rig.transform.position;
            Vector3 to = from + new Vector3(shove.DirectionX, 0f, shove.DirectionZ) * shove.Meters;
            if (target.Moving) EndSegment(target, false);
            target.Rig.Movement.TeleportTo(to);
            target.LastPos = target.Rig.transform.position;
            target.Shoves++;
            _log("dev-shove applied id=" + target.PlayerId + " slot=" + target.Slot + " from=" + P(from) + " to=" + P(target.LastPos) +
                 " meters=" + F(shove.Meters) + " tick=" + _tick);
        }
#endif

        private void TrackSegment(MoveState state, Vector3 pos, float distance, float speed)
        {
            if (!state.Moving)
            {
                if (speed <= SegStartSpeed) return;

                state.Moving = true;
                state.SegStartTick = _tick;
                state.SegFrom = state.LastPos;
                state.SegTo = pos;
                state.SegDist = distance;
                state.SegPeak = speed;
                state.SegTicks = 1;
                state.StillTicks = 0;
                return;
            }

            state.SegDist += distance;
            state.SegTicks++;
            if (speed > state.SegPeak) state.SegPeak = speed;

            if (speed < SegStopSpeed)
            {
                state.StillTicks++;
                if (state.StillTicks >= SegStopTicks) EndSegment(state, false);
            }
            else
            {
                state.StillTicks = 0;
                state.SegTo = pos;
            }
        }

        private void EndSegment(MoveState state, bool open)
        {
            int movingTicks = Mathf.Max(1, state.SegTicks - state.StillTicks);
            float dx = state.SegTo.x - state.SegFrom.x;
            float dz = state.SegTo.z - state.SegFrom.z;
            float displacement = Mathf.Sqrt(dx * dx + dz * dz);
            float mean = state.SegDist / (movingTicks * NetTuning.TickSeconds);

            _log("seg id=" + state.PlayerId + " kind=move startTick=" + state.SegStartTick + " endTick=" + _tick +
                 " ticks=" + movingTicks + " secs=" + F(movingTicks * NetTuning.TickSeconds) +
                 " dist=" + F(state.SegDist) + " disp=" + F(displacement) + " mean=" + F(mean) + " peak=" + F(state.SegPeak) +
                 " from=" + P(state.SegFrom) + " to=" + P(state.SegTo) + (open ? " open=1" : ""));
            state.Moving = false;
            state.StillTicks = 0;
        }

        private void TrackAir(MoveState state, PlayerMovement movement, Vector3 pos, float dy)
        {
            if (!movement.IsGrounded)
            {
                if (state.AirTicks == 0)
                {
                    state.AirYMax = pos.y;
                    state.AirRest = state.RestY;
                }
                else if (pos.y > state.AirYMax)
                {
                    state.AirYMax = pos.y;
                }

                state.AirTicks++;
                state.AirTicksTotal++;
                return;
            }

            if (state.AirTicks >= AirMinTicks)
            {
                _log("air id=" + state.PlayerId + " ticks=" + state.AirTicks + " rest=" + F(state.AirRest) + " ymax=" + F(state.AirYMax) +
                     " rise=" + F(state.AirYMax - state.AirRest) + " tick=" + _tick);
            }

            state.AirTicks = 0;
            if (Mathf.Abs(dy) < 0.005f) state.RestY = pos.y;
        }

        // ---- logging ---------------------------------------------------------------------------

        private static long Signature(MoveState s)
        {
            InputSanitizer z = s.Sanitizer;
            return z.Accepted + 1000L * (z.DroppedReplay + 1000L * (z.DroppedFlood + 1000L * (z.DroppedInvalid + 1000L * (s.Forge + 1000L * s.Buffer.Overflowed))));
        }

        private void MaybeLog(MoveState s)
        {
            if (_tick < s.NextLogTick) return;
            s.NextLogTick = _tick + LogEveryTicks;

            Vector3 delta = s.LastPos - s.LoggedPos;
            bool moved = delta.sqrMagnitude > LogMoveEpsilon * LogMoveEpsilon;
            bool counters = Signature(s) != s.LoggedSignature;
            bool first = s.LastLogTick < 0;
            bool idle = _tick - s.LastLogTick >= LogIdleTicks;
            if (!(moved || counters || first || idle)) return;

            WriteMoveLine(s, false, null);
        }

        /// <summary>
        /// Diagnostic for the intermittent CI loss of the whole server receive path (U3A4b.md): one line when a player goes quiet
        /// (30 ticks), then every 90 ticks while it stays quiet. It lists who ELSE is still delivering frames and the longest
        /// gap between two server ticks, so a stalled server process (gap) is told apart from a dead receive path (large gap absent,
        /// every player quiet at the same tick).
        /// </summary>
        private void MaybeLogQuiet(MoveState s)
        {
            long quiet = _tick - s.LastFrameTick;
            if (quiet < QuietSendTicks || _tick < s.NextQuietLogTick) return;
            s.NextQuietLogTick = _tick + QuietLogTicks;

            string others = "";
            for (int i = 0; i < _order.Count; i++)
            {
                MoveState o = _order[i];
                if (o == s) continue;
                others += (others.Length > 0 ? "," : "") + o.PlayerId + ":" + (_tick - o.LastFrameTick);
            }

            IReadOnlyList<ulong> ids = _nm != null ? _nm.ConnectedClientsIds : null;
            string connected = "";
            if (ids != null)
                for (int i = 0; i < ids.Count; i++) connected += (connected.Length > 0 ? "," : "") + ids[i];

            _log("rx-quiet id=" + s.PlayerId + " client=" + s.ClientId + " quietTicks=" + quiet + " others=[" + others + "] connected=[" + connected +
                 "] maxGapMs=" + (_maxGapSinceQuietLog * 1000f).ToString("F0", CultureInfo.InvariantCulture) +
                 " acksSuppressed=" + s.AcksSuppressed + " paramsSuppressed=" + s.ParamsSuppressed + " tick=" + _tick);
            _maxGapSinceQuietLog = 0f;
        }

        private void WriteMoveLine(MoveState s, bool final, string reason)
        {
            InputSanitizer z = s.Sanitizer;
            string line = "move id=" + s.PlayerId + " pos=" + P(s.LastPos) +
                          " acc=" + z.Accepted + " rep=" + z.DroppedReplay + " flood=" + z.DroppedFlood + " inv=" + z.DroppedInvalid +
                          " tick=" + _tick + " yaw=" + F(s.Rig != null ? MoveInputFrame.WrapDegrees(s.Rig.transform.eulerAngles.y) : 0f) +
                          " clamp=" + z.Clamped + " qdrop=" + s.Buffer.Overflowed + " forge=" + s.Forge +
                          " vmax=" + F(s.VMaxSinceLog) + " ymax=" + F(float.IsNegativeInfinity(s.YMaxSinceLog) ? s.LastPos.y : s.YMaxSinceLog) +
                          " air=" + s.AirTicksTotal + " depth=" + s.Buffer.Depth;
            if (final)
            {
                line += " final=1 reason=" + (reason ?? "-") + " vmaxAll=" + F(s.VMax) +
                        " nan=" + z.InvalidNonFinite + " big=" + z.InvalidMagnitude + " seqj=" + z.InvalidSeqWindow +
                        " seq0=" + z.InvalidSeqZero + " flags=" + z.InvalidFlags + " badbatch=" + s.BadBatch +
                        " redundant=" + z.Redundant + " repeated=" + s.Buffer.RepeatedTicks + " neutral=" + s.Buffer.NeutralTicks +
                        " noplayer=" + _noPlayer + " acks=" + s.AcksSent + " shoves=" + s.Shoves +
                        " acksSuppressed=" + s.AcksSuppressed + " paramsSuppressed=" + s.ParamsSuppressed;
            }

            _log(line);

            s.LastLogTick = _tick;
            s.LoggedPos = s.LastPos;
            s.LoggedSignature = Signature(s);
            s.VMaxSinceLog = 0f;
            s.YMaxSinceLog = float.NegativeInfinity;
        }

        private static string F(float value) { return value.ToString("F3", CultureInfo.InvariantCulture); }

        private static string P(Vector3 v) { return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")"; }
    }
}
