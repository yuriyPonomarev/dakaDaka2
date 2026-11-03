using System;
using System.Collections.Generic;
using System.Globalization;

namespace Emberdelve.Net.Rules
{
    public enum NetScriptKind
    {
        None = 0,
        /// <summary>U3A3: call the forge-owner probe RPC on another player's object. A script of its own, never chained.</summary>
        ForgeOwner = 1,
        WalkForward = 2,
        WalkSquare = 3,
        SprintForward = 4,
        Jump = 5,
        /// <summary>Ten frame batches per tick, move neutral.</summary>
        FloodInput = 6,
        /// <summary>Ten frame batches per tick, move forward (shows a flood cannot out-run one honest stream).</summary>
        FloodRun = 7,
        /// <summary>Re-send sequence numbers from far in the past, move forward.</summary>
        ReplaySeq = 8,
        /// <summary>NaN / Infinity axes and angles.</summary>
        NanInput = 9,
        /// <summary>|move| far above 1.</summary>
        BigMove = 10,
        /// <summary>Sequence numbers jumped far ahead.</summary>
        SeqJump = 11,
        /// <summary>Movement frames sent on ANOTHER player's object through the development RPC (RequireOwnership off).</summary>
        ForgeMove = 12,
        /// <summary>Development probe (U3B1): forged / own / replayed / malformed / cooling-down skill requests. Never chained.</summary>
        SkillProof = 13,
        /// <summary>U3B2: a scripted key press, the production path: slot 0, slot 0 again (refused while cooling down), slot 1. Never chained.</summary>
        SkillCast = 14,
        /// <summary>U3B2 development probe: forged, replayed, malformed and flooded skill requests until the server disconnects the client. Never chained.</summary>
        SkillAbuse = 15
    }

    /// <summary>One step of a client test script: <c>name[:seconds][@yawDegrees]</c>.</summary>
    public sealed class NetScriptStep
    {
        public NetScriptKind Kind;
        public string Name = "";
        /// <summary>Seconds (walk-square: per side).</summary>
        public float Seconds;
        public bool HasYaw;
        public float Yaw;

        /// <summary>How long the step runs (walk-square has four sides).</summary>
        public float Duration => Kind == NetScriptKind.WalkSquare ? Seconds * 4f : Seconds;

        public bool IsHostile =>
            Kind == NetScriptKind.FloodInput || Kind == NetScriptKind.FloodRun || Kind == NetScriptKind.ReplaySeq ||
            Kind == NetScriptKind.NanInput || Kind == NetScriptKind.BigMove || Kind == NetScriptKind.SeqJump ||
            Kind == NetScriptKind.ForgeMove;
    }

    /// <summary>
    /// Parsed <c>-ember-net-script</c>: one step or a comma separated chain, e.g. <c>walk-forward:3@90,sprint-forward:1.5@270,jump</c>.
    /// Names: forge-owner, skill-proof, skill-cast, skill-abuse (single-step probes), walk-forward, walk-square, sprint-forward, jump,
    /// flood-input, flood-run, replay-seq, nan-input, big-move, seq-jump, forge-move. <c>:seconds</c> overrides the default duration, <c>@yaw</c> the view yaw (absolute degrees, world Y;
    /// 0 looks along +Z, 90 along +X). A chain idles <see cref="GapSeconds"/> between steps so a server can tell the movements apart.
    /// Pure data (no engine dependency).
    /// </summary>
    public sealed class NetScriptSpec
    {
        public const float GapSeconds = 0.8f;
        public const float MaxStepSeconds = 120f;

        private readonly List<NetScriptStep> _steps = new List<NetScriptStep>();

        public IReadOnlyList<NetScriptStep> Steps => _steps;

        /// <summary>The original (lower-cased, trimmed) text.</summary>
        public string Text { get; private set; } = "";

        public bool IsForgeOwnerOnly => _steps.Count == 1 && _steps[0].Kind == NetScriptKind.ForgeOwner;
        public bool IsSkillProofOnly => _steps.Count == 1 && _steps[0].Kind == NetScriptKind.SkillProof;
        public bool IsSkillCastOnly => _steps.Count == 1 && _steps[0].Kind == NetScriptKind.SkillCast;
        public bool IsSkillAbuseOnly => _steps.Count == 1 && _steps[0].Kind == NetScriptKind.SkillAbuse;

        /// <summary>True for the scripts that drive an RPC probe or the skill channel instead of the movement stream (always a single step).</summary>
        public bool IsProbeOnly => _steps.Count == 1 && IsProbeKind(_steps[0].Kind);

        /// <summary>True for the scripts that run on the skill sender (not the movement sender).</summary>
        public bool IsSkillScript => IsSkillProofOnly || IsSkillCastOnly || IsSkillAbuseOnly;

        /// <summary>The kinds that stand alone: no duration, no yaw semantics, never part of a chain.</summary>
        public static bool IsProbeKind(NetScriptKind kind)
        {
            return kind == NetScriptKind.ForgeOwner || kind == NetScriptKind.SkillProof ||
                   kind == NetScriptKind.SkillCast || kind == NetScriptKind.SkillAbuse;
        }

        /// <summary>
        /// Probe kinds that only exist in development builds and the Editor (hostile traffic): a release build must say so instead of
        /// pretending. SkillCast is a plain scripted key press on the production path and is available everywhere.
        /// </summary>
        public static bool IsDevelopmentOnly(NetScriptKind kind)
        {
            return kind == NetScriptKind.ForgeOwner || kind == NetScriptKind.SkillProof || kind == NetScriptKind.SkillAbuse;
        }

        /// <summary>Seconds from the start until the last step and its trailing gap are over.</summary>
        public float TotalSeconds
        {
            get
            {
                float total = 0f;
                for (int i = 0; i < _steps.Count; i++) total += _steps[i].Duration + GapSeconds;
                return total;
            }
        }

        public static string NameOf(NetScriptKind kind)
        {
            switch (kind)
            {
                case NetScriptKind.ForgeOwner: return "forge-owner";
                case NetScriptKind.SkillProof: return "skill-proof";
                case NetScriptKind.SkillCast: return "skill-cast";
                case NetScriptKind.SkillAbuse: return "skill-abuse";
                case NetScriptKind.WalkForward: return "walk-forward";
                case NetScriptKind.WalkSquare: return "walk-square";
                case NetScriptKind.SprintForward: return "sprint-forward";
                case NetScriptKind.Jump: return "jump";
                case NetScriptKind.FloodInput: return "flood-input";
                case NetScriptKind.FloodRun: return "flood-run";
                case NetScriptKind.ReplaySeq: return "replay-seq";
                case NetScriptKind.NanInput: return "nan-input";
                case NetScriptKind.BigMove: return "big-move";
                case NetScriptKind.SeqJump: return "seq-jump";
                case NetScriptKind.ForgeMove: return "forge-move";
                default: return "";
            }
        }

        private static bool TryKind(string name, out NetScriptKind kind, out float defaultSeconds)
        {
            defaultSeconds = 1.5f;
            switch (name)
            {
                case "forge-owner": kind = NetScriptKind.ForgeOwner; defaultSeconds = 0f; return true;
                case "skill-proof": kind = NetScriptKind.SkillProof; defaultSeconds = 0f; return true;
                case "skill-cast": kind = NetScriptKind.SkillCast; defaultSeconds = 0f; return true;
                case "skill-abuse": kind = NetScriptKind.SkillAbuse; defaultSeconds = 0f; return true;
                case "walk-forward": kind = NetScriptKind.WalkForward; defaultSeconds = 3f; return true;
                case "walk-square": kind = NetScriptKind.WalkSquare; defaultSeconds = 1f; return true;
                case "sprint-forward": kind = NetScriptKind.SprintForward; defaultSeconds = 3f; return true;
                case "jump": kind = NetScriptKind.Jump; defaultSeconds = 1.5f; return true;
                case "flood-input": kind = NetScriptKind.FloodInput; defaultSeconds = 1.5f; return true;
                case "flood-run": kind = NetScriptKind.FloodRun; defaultSeconds = 2f; return true;
                case "replay-seq": kind = NetScriptKind.ReplaySeq; defaultSeconds = 1.5f; return true;
                case "nan-input": kind = NetScriptKind.NanInput; defaultSeconds = 1.5f; return true;
                case "big-move": kind = NetScriptKind.BigMove; defaultSeconds = 1.5f; return true;
                case "seq-jump": kind = NetScriptKind.SeqJump; defaultSeconds = 1.5f; return true;
                case "forge-move": kind = NetScriptKind.ForgeMove; defaultSeconds = 1.5f; return true;
                default: kind = NetScriptKind.None; return false;
            }
        }

        public static bool TryParse(string text, out NetScriptSpec spec, out string error)
        {
            spec = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "empty script"; return false; }

            NetScriptSpec result = new NetScriptSpec { Text = text.Trim().ToLowerInvariant() };
            string[] parts = result.Text.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0) { error = "empty step in '" + text + "'"; return false; }

                string yawText = null;
                int at = part.IndexOf('@');
                if (at >= 0)
                {
                    yawText = part.Substring(at + 1);
                    part = part.Substring(0, at);
                }

                string secondsText = null;
                int colon = part.IndexOf(':');
                if (colon >= 0)
                {
                    secondsText = part.Substring(colon + 1);
                    part = part.Substring(0, colon);
                }

                if (!TryKind(part, out NetScriptKind kind, out float seconds))
                {
                    error = "unknown script '" + part + "'";
                    return false;
                }

                NetScriptStep step = new NetScriptStep { Kind = kind, Name = part, Seconds = seconds };

                if (secondsText != null)
                {
                    float parsed = 0f;
                    if (IsProbeKind(kind) ||
                        !TryFloat(secondsText, out parsed) || parsed <= 0f || parsed > MaxStepSeconds)
                    {
                        error = "invalid seconds '" + secondsText + "' for " + part + " (0.." + MaxStepSeconds + ")";
                        return false;
                    }

                    step.Seconds = parsed;
                }

                if (yawText != null)
                {
                    float yaw = 0f;
                    if (!TryFloat(yawText, out yaw))
                    {
                        error = "invalid yaw '" + yawText + "' for " + part;
                        return false;
                    }

                    step.HasYaw = true;
                    step.Yaw = MoveInputFrame.WrapDegrees(yaw);
                }

                result._steps.Add(step);
            }

            if (result._steps.Count > 1)
            {
                for (int i = 0; i < result._steps.Count; i++)
                {
                    if (IsProbeKind(result._steps[i].Kind))
                    {
                        error = result._steps[i].Name + " cannot be chained";
                        return false;
                    }
                }
            }

            spec = result;
            return true;
        }

        private static bool TryFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                   !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    /// <summary>What the script wants the client to do this render frame.</summary>
    public struct NetScriptIntent
    {
        /// <summary>The whole script (all steps and gaps) is over.</summary>
        public bool Done;
        /// <summary>Index of the running step, or -1 in a gap / before the start / after the end.</summary>
        public int ActiveStep;
        public NetScriptKind ActiveKind;
        public float MoveX;
        public float MoveY;
        public bool Sprint;
        /// <summary>True for exactly one Evaluate call per jump step (an edge).</summary>
        public bool Jump;
        public float Yaw;
    }

    /// <summary>
    /// Time line of a <see cref="NetScriptSpec"/>: given seconds since the start, says which step runs and what input it produces.
    /// Stateful only for the jump edge. Pure, so the shape of every script is unit tested without a process.
    /// </summary>
    public sealed class NetScriptTimeline
    {
        private readonly NetScriptSpec _spec;
        private readonly float _defaultYaw;
        private readonly float[] _starts;
        private int _jumpFiredFor = -1;

        public NetScriptTimeline(NetScriptSpec spec, float defaultYaw)
        {
            _spec = spec;
            _defaultYaw = MoveInputFrame.WrapDegrees(defaultYaw);
            _starts = new float[spec.Steps.Count];
            float t = 0f;
            for (int i = 0; i < _starts.Length; i++)
            {
                _starts[i] = t;
                t += spec.Steps[i].Duration + NetScriptSpec.GapSeconds;
            }
        }

        public float TotalSeconds => _spec.TotalSeconds;

        /// <summary>The view yaw to hold before the script starts (the first step's yaw, so the body already faces the right way).</summary>
        public float YawBeforeStart => StepYaw(0);

        /// <summary>Number of steps whose running time has fully passed at <paramref name="elapsed"/>.</summary>
        public int CompletedSteps(float elapsed)
        {
            int done = 0;
            for (int i = 0; i < _starts.Length; i++)
                if (elapsed >= _starts[i] + _spec.Steps[i].Duration) done = i + 1;
            return done;
        }

        public NetScriptIntent Evaluate(float elapsed)
        {
            NetScriptIntent intent = new NetScriptIntent { ActiveStep = -1, Yaw = YawBeforeStart };
            if (_starts.Length == 0) { intent.Done = true; return intent; }
            if (elapsed < 0f) return intent;

            int started = -1;
            for (int i = 0; i < _starts.Length; i++)
                if (elapsed >= _starts[i]) started = i;

            intent.Yaw = StepYaw(started);
            NetScriptStep step = _spec.Steps[started];
            float local = elapsed - _starts[started];

            if (local >= step.Duration)
            {
                // The gap after the step (or the end of the script): hold the view, no input.
                if (started == _starts.Length - 1 && local >= step.Duration + NetScriptSpec.GapSeconds) intent.Done = true;
                if (step.Kind == NetScriptKind.WalkSquare) intent.Yaw = Wrap(StepYaw(started) + 90f * 3f);
                return intent;
            }

            intent.ActiveStep = started;
            intent.ActiveKind = step.Kind;

            switch (step.Kind)
            {
                case NetScriptKind.WalkForward:
                    intent.MoveY = 1f;
                    break;
                case NetScriptKind.SprintForward:
                    intent.MoveY = 1f;
                    intent.Sprint = true;
                    break;
                case NetScriptKind.WalkSquare:
                    {
                        int side = (int)(local / step.Seconds);
                        if (side > 3) side = 3;
                        intent.MoveY = 1f;
                        intent.Yaw = Wrap(StepYaw(started) + 90f * side);
                    }
                    break;
                case NetScriptKind.Jump:
                    if (_jumpFiredFor != started)
                    {
                        _jumpFiredFor = started;
                        intent.Jump = true;
                    }
                    break;
                case NetScriptKind.FloodRun:
                    // The honest stream itself runs forward; the sender multiplies it by ten per tick.
                    intent.MoveY = 1f;
                    break;
                case NetScriptKind.ReplaySeq:
                case NetScriptKind.SeqJump:
                case NetScriptKind.ForgeMove:
                    // Hostile steps: the honest stream stays idle; the sender adds the hostile frames (forward intent).
                    break;
            }

            return intent;
        }

        private float StepYaw(int index)
        {
            if (index < 0) index = 0;
            if (index >= _spec.Steps.Count) return _defaultYaw;
            NetScriptStep step = _spec.Steps[index];
            return step.HasYaw ? step.Yaw : _defaultYaw;
        }

        private static float Wrap(float degrees) { return MoveInputFrame.WrapDegrees(degrees); }
    }
}
