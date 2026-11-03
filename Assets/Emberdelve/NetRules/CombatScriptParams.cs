using System;
using System.Collections.Generic;
using System.Globalization;

namespace Emberdelve.Net.Rules
{
    /// <summary>
    /// U3B3: the parameters of a scripted combat client (<c>melee-duel</c>, <c>ranged-shot</c>, <c>combat-abuse</c>), read from the CONTENT of
    /// its start file (the file whose appearance starts the script). Whitespace or ';' separated <c>key=value</c> tokens; the first token may be
    /// a bare word ("go"). A process gate writes the numbers it derived from the authored data and from the server's logged positions
    /// (distances, hold times, the drop-compensated pitch) into the file, so no number is typed into a client. A client's own choice of these
    /// values is just input: the server never believes any of them.
    ///   stop=M        melee: approach the other player until the horizontal distance is at most M (default 1.2)
    ///   interval=S    melee: seconds between attack presses (default 1.4)
    ///   presses=N     melee: most attack presses of the whole script (default 40)
    ///   walk=YAW:S    walk along world yaw YAW for S seconds before anything else (default: none)
    ///   away=M        ranged: the shot distance. With awayyaw this player walks along that yaw until the distance to the other player is at
    ///                 least M (times out after 10 s); without it the shooter waits (standing) for the other player to be that far away
    ///   awayyaw=YAW   world yaw (degrees) of the walk that makes the distance (the gate picks the player with open ground along it)
    ///   shots=N       ranged: full-charge shots (held for <c>hold</c>) before the final early release (default 1)
    ///   hold=S        ranged: seconds each full shot is held (default 1.2); after them one more draw is released after <c>early</c> instead
    ///   early=S       ranged: hold of the last shot (default 0.1: a release before the minimum charge)
    ///   pspeed=V pgrav=G   ranged: muzzle speed and gravity (from the weapon data) so the client aims with drop compensation
    ///   pitch=DEG     ranged: a fixed aim pitch instead of the computed one
    ///   aimheight=M   ranged: height above the target's feet to aim at (default 1.1)
    /// Pure data; never throws.
    /// </summary>
    public sealed class CombatScriptParams
    {
        public float Stop = 1.2f;
        public float Interval = 1.4f;
        public int Presses = 40;
        public bool HasWalk;
        public float WalkYaw;
        public float WalkSeconds;
        public float Away;
        public bool HasAwayYaw;
        public float AwayYaw;
        public int Shots = 1;
        public float Hold = 1.2f;
        public float Early = 0.1f;
        public float ProjectileSpeed;
        public float ProjectileGravity;
        public bool HasPitch;
        public float Pitch;
        public float AimHeight = 1.1f;

        public static CombatScriptParams Default { get { return new CombatScriptParams(); } }

        public static bool TryParse(string text, out CombatScriptParams result, out string error)
        {
            result = new CombatScriptParams();
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;

            string[] tokens = text.Split(new[] { ' ', '\t', '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                int eq = token.IndexOf('=');
                if (eq <= 0) continue; // a bare word such as "go"

                string key = token.Substring(0, eq).ToLowerInvariant();
                string value = token.Substring(eq + 1);
                bool ok = true;
                switch (key)
                {
                    case "stop": ok = Positive(value, out result.Stop, 0.3f, 50f); break;
                    case "interval": ok = Positive(value, out result.Interval, 0.05f, 30f); break;
                    case "presses": ok = Int(value, out result.Presses, 1, 1000); break;
                    case "shots": ok = Int(value, out result.Shots, 1, 20); break;
                    case "hold": ok = Positive(value, out result.Hold, 0.01f, 60f); break;
                    case "early": ok = Positive(value, out result.Early, 0.0f, 60f); break;
                    case "away": ok = Positive(value, out result.Away, 0.0f, 200f); break;
                    case "awayyaw":
                        ok = Number(value, out result.AwayYaw);
                        result.HasAwayYaw = ok;
                        break;
                    case "pspeed": ok = Positive(value, out result.ProjectileSpeed, 0.0f, 500f); break;
                    case "pgrav": ok = Positive(value, out result.ProjectileGravity, 0.0f, 100f); break;
                    case "aimheight": ok = Positive(value, out result.AimHeight, 0.0f, 3f); break;
                    case "pitch":
                        ok = Number(value, out result.Pitch) && Math.Abs(result.Pitch) <= 90f;
                        result.HasPitch = ok;
                        break;
                    case "walk":
                        {
                            int colon = value.IndexOf(':');
                            ok = colon > 0 && Number(value.Substring(0, colon), out result.WalkYaw) &&
                                 Positive(value.Substring(colon + 1), out result.WalkSeconds, 0.05f, 60f);
                            result.HasWalk = ok;
                        }

                        break;
                }

                if (!ok)
                {
                    error = "invalid value '" + value + "' for " + key;
                    result = new CombatScriptParams();
                    return false;
                }
            }

            return true;
        }

        private static bool Number(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool Positive(string text, out float value, float min, float max)
        {
            return Number(text, out value) && value >= min && value <= max;
        }

        private static bool Int(string text, out int value, int min, int max)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
        }
    }
}
