using System;

namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// One tick of movement INTENT. A client can only ever send this (axes, view yaw/pitch, three flags); the
    /// authority derives speed, collision and position from it with the real PlayerMovement.Step. Plain data with no
    /// engine or package dependency, so it lives in the pure NetRules assembly that both Emberdelve.Runtime
    /// (PlayerMovement.Step) and Emberdelve.Net (the wire layer, U3A4) reference.
    ///
    /// In memory the axes are full-precision floats (the offline Update path builds one of these every render frame and
    /// must behave exactly as before). <see cref="Quantized"/> / the Quantize* helpers give the lossy form that goes on
    /// the wire, and the form the server applies, so a predicting client can run the same numbers.
    /// </summary>
    public struct MoveInputFrame
    {
        /// <summary>Sprint is held.</summary>
        public const byte FlagSprint = 1 << 0;
        /// <summary>Jump pressed since the previous frame (an edge, not a level).</summary>
        public const byte FlagJump = 1 << 1;
        /// <summary>Crouch toggled since the previous frame (an edge).</summary>
        public const byte FlagCrouchToggle = 1 << 2;
        /// <summary>Reserved (guard held); carried so the wire format does not change when U3B needs it.</summary>
        public const byte FlagGuardHeld = 1 << 3;
        /// <summary>Every flag bit the server accepts; anything else is a malformed frame.</summary>
        public const byte KnownFlags = FlagSprint | FlagJump | FlagCrouchToggle | FlagGuardHeld;

        public const float DefaultMinPitch = -85f;
        public const float DefaultMaxPitch = 85f;

        /// <summary>Client-assigned, strictly increasing per sender; the server drops old or replayed values.</summary>
        public uint Seq;
        /// <summary>Strafe axis, right positive, nominally [-1, 1].</summary>
        public float MoveX;
        /// <summary>Forward axis, forward positive, nominally [-1, 1].</summary>
        public float MoveY;
        /// <summary>Absolute view yaw in degrees (world Y rotation). Movement is relative to it.</summary>
        public float Yaw;
        /// <summary>Absolute view pitch in degrees (carried for the authoritative aim pose, U3B).</summary>
        public float Pitch;
        public byte Flags;

        public bool Sprint => (Flags & FlagSprint) != 0;
        public bool Jump => (Flags & FlagJump) != 0;
        public bool CrouchToggle => (Flags & FlagCrouchToggle) != 0;
        public bool GuardHeld => (Flags & FlagGuardHeld) != 0;

        public static byte MakeFlags(bool sprint, bool jump, bool crouchToggle, bool guardHeld = false)
        {
            byte flags = 0;
            if (sprint) flags |= FlagSprint;
            if (jump) flags |= FlagJump;
            if (crouchToggle) flags |= FlagCrouchToggle;
            if (guardHeld) flags |= FlagGuardHeld;
            return flags;
        }

        public static MoveInputFrame Create(uint seq, float moveX, float moveY, float yaw, float pitch,
            bool sprint, bool jump, bool crouchToggle)
        {
            return new MoveInputFrame
            {
                Seq = seq,
                MoveX = moveX,
                MoveY = moveY,
                Yaw = yaw,
                Pitch = pitch,
                Flags = MakeFlags(sprint, jump, crouchToggle)
            };
        }

        /// <summary>True when every float is finite and no unknown flag bit is set.</summary>
        public bool IsWellFormed =>
            IsFinite(MoveX) && IsFinite(MoveY) && IsFinite(Yaw) && IsFinite(Pitch) && (Flags & ~KnownFlags) == 0;

        public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // ---- quantization (wire form) -----------------------------------------------------------------------------

        /// <summary>Axis in [-1, 1] to a signed byte (+-127). Non-finite becomes 0, out of range is clamped.</summary>
        public static sbyte QuantizeAxis(float value)
        {
            if (!IsFinite(value)) return 0;
            if (value > 1f) value = 1f;
            if (value < -1f) value = -1f;
            return (sbyte)Math.Round(value * 127f, MidpointRounding.AwayFromZero);
        }

        public static float DequantizeAxis(sbyte value)
        {
            if (value == -128) value = -127; // -128 is not producible by QuantizeAxis; treat it as the minimum
            return value / 127f;
        }

        /// <summary>Wraps any angle into [-180, 180). Non-finite becomes 0.</summary>
        public static float WrapDegrees(float degrees)
        {
            if (!IsFinite(degrees)) return 0f;
            double wrapped = degrees % 360.0;
            if (wrapped >= 180.0) wrapped -= 360.0;
            if (wrapped < -180.0) wrapped += 360.0;
            return (float)wrapped;
        }

        /// <summary>Degrees to a 16-bit code (0.0055 deg steps over the full circle).</summary>
        public static ushort QuantizeAngle(float degrees)
        {
            double wrapped = WrapDegrees(degrees);
            double turns = (wrapped + 180.0) / 360.0; // [0, 1)
            int code = (int)Math.Round(turns * 65536.0, MidpointRounding.AwayFromZero);
            return (ushort)(code & 0xFFFF);
        }

        public static float DequantizeAngle(ushort code) => (float)(code * (360.0 / 65536.0) - 180.0);

        public static float ClampPitch(float pitch, float min = DefaultMinPitch, float max = DefaultMaxPitch)
        {
            if (!IsFinite(pitch)) return 0f;
            return pitch < min ? min : (pitch > max ? max : pitch);
        }

        /// <summary>
        /// The frame as the server will see it after the wire: axes and angles quantized, flags masked to the known
        /// set, pitch clamped. A predicting client steps with this so both sides run the same numbers.
        /// </summary>
        public MoveInputFrame Quantized()
        {
            return new MoveInputFrame
            {
                Seq = Seq,
                MoveX = DequantizeAxis(QuantizeAxis(MoveX)),
                MoveY = DequantizeAxis(QuantizeAxis(MoveY)),
                Yaw = DequantizeAngle(QuantizeAngle(Yaw)),
                Pitch = ClampPitch(DequantizeAngle(QuantizeAngle(Pitch))),
                Flags = (byte)(Flags & KnownFlags)
            };
        }
    }
}
