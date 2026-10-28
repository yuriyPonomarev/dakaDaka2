using System;
using System.Globalization;

namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// U3A4b development switch (server, development builds / Editor only; the gate's forced-correction scenario):
    /// <c>-ember-net-dev-shove SLOT:YAW[:METERS]</c> teleports the player admitted with that dev slot once, METERS (default 2) along
    /// the world direction YAW (degrees, 0 = +Z, 90 = +X) when the file named by <c>-ember-net-dev-shove-file</c> appears. The
    /// owner's prediction cannot know about it, so it must snap. Pure parsing only.
    /// </summary>
    public sealed class NetDevShove
    {
        public const float DefaultMeters = 2f;
        public const float MaxMeters = 10f;

        public string Slot { get; private set; } = "";
        public float YawDegrees { get; private set; }
        public float Meters { get; private set; } = DefaultMeters;

        /// <summary>World X component of the unit direction.</summary>
        public float DirectionX => (float)Math.Sin(YawDegrees * Math.PI / 180.0);
        /// <summary>World Z component of the unit direction.</summary>
        public float DirectionZ => (float)Math.Cos(YawDegrees * Math.PI / 180.0);

        public static bool TryParse(string text, out NetDevShove result, out string error)
        {
            result = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "empty"; return false; }

            string[] parts = text.Trim().Split(':');
            if (parts.Length < 2 || parts.Length > 3) { error = "expected SLOT:YAW[:METERS]"; return false; }

            string slot = parts[0].Trim();
            if (slot.Length == 0) { error = "empty slot"; return false; }

            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float yaw) ||
                float.IsNaN(yaw) || float.IsInfinity(yaw))
            {
                error = "invalid yaw '" + parts[1] + "'";
                return false;
            }

            float meters = DefaultMeters;
            if (parts.Length == 3 &&
                (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out meters) ||
                 float.IsNaN(meters) || float.IsInfinity(meters) || meters <= 0f || meters > MaxMeters))
            {
                error = "invalid meters '" + parts[2] + "' (0 < m <= " + MaxMeters.ToString(CultureInfo.InvariantCulture) + ")";
                return false;
            }

            result = new NetDevShove { Slot = slot, YawDegrees = MoveInputFrame.WrapDegrees(yaw), Meters = meters };
            return true;
        }
    }
}
