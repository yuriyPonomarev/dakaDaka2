using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Player
{
    /// <summary>
    /// How a cut is SHOWN (first-person arms and other bodies alike); the damage segment stays the pattern's own.
    /// Our own motion, shaped after the user's reference recording (18 Nov 2026 capture, sword and shield): in the wind-up the
    /// weapon is drawn back past the side the cut starts from, out of the view; in the strike the hand leads across in front of the
    /// body and the blade trails behind it; after the strike the hand carries on past the far side, then comes back to the ready pose.
    /// The direction is the pattern's (where its blade points at the start and at the end of the damaging window), so every authored
    /// cut gets its own motion. A thrust (no sweep) is drawn back beside the body and driven straight out.
    /// Positions and directions are in the aim frame (the eye, looking along +Z).
    /// </summary>
    public static class SwingMotion
    {
        /// <summary>Where the motion takes place for one kind of viewer.</summary>
        public struct Frame
        {
            /// <summary>Middle of the hand's path.</summary>
            public Vector3 Centre;
            /// <summary>Half of the path's length for a cut across and for a cut down or up (the view is wider than tall), and how far forward its middle bulges.</summary>
            public float HalfLength, HalfHeight, Bulge;
            /// <summary>Drawn back for the wind-up: towards the body, upwards, and further out to the starting side.</summary>
            public float CockDepth, CockRise, CockOut;
            /// <summary>Past the far side after the strike: how far the hand wraps back towards the body.</summary>
            public float ThroughDepth;
            /// <summary>
            /// The player's own view: drawn back, the weapon is outside the picture, at one of these points (beside the head on
            /// the weapon side, low for a cut from the off side, above for a downward cut). The hand never goes far to the off
            /// side during a cut: the arm would lie across the whole screen (user recording of 25 Nov 2026).
            /// </summary>
            public bool CockBesideCamera;
            public Vector3 CockPoint, CockOffSide, CockAbove;
            /// <summary>A thrust: the hand drawn back beside the body, and at full extension (moved with the thrust's own direction).</summary>
            public Vector3 ThrustCock, ThrustEnd;
            /// <summary>The player's own view of a weapon held in both hands: its own motion (<see cref="SampleBothHands"/>).</summary>
            public bool BothHandsInView;
            /// <summary>Shift of that motion for this viewer (a body holds the hands lower than the camera sees them).</summary>
            public Vector3 BothHandsOffset;
            /// <summary>How far below the eye line the point of a thrust is aimed (a body thrusts from the chest, not from the eyes).</summary>
            public float ThrustDrop;
        }

        /// <summary>The player's own view: the path crosses the screen below its centre, close to the camera.</summary>
        public static readonly Frame FirstPerson = new Frame
        { Centre = new Vector3(0.14f, 0.00f, 0.30f), HalfLength = 0.17f, HalfHeight = 0.17f, Bulge = 0.07f, CockDepth = 0.06f, CockRise = 0.22f, CockOut = 0.55f, ThroughDepth = 0.10f,
          CockBesideCamera = true, CockPoint = new Vector3(0.52f, 0.24f, 0.20f), CockOffSide = new Vector3(-0.05f, -0.42f, 0.20f), CockAbove = new Vector3(0.34f, 0.44f, 0.22f),
          ThrustCock = new Vector3(0.36f, -0.24f, 0.10f), ThrustEnd = new Vector3(0.10f, -0.15f, 0.64f) };

        /// <summary>The player's own view with both hands on the handle: a narrower path; drawn back, the hands wait low on the weapon side, below the picture (up beside the head the second forearm lies across the whole view).</summary>
        public static readonly Frame FirstPersonTwoHanded = new Frame
        { Centre = new Vector3(0.10f, -0.02f, 0.32f), HalfLength = 0.16f, HalfHeight = 0.16f, Bulge = 0.07f, CockDepth = 0.06f, CockRise = 0.22f, CockOut = 0.45f, ThroughDepth = 0.10f,
          BothHandsInView = true,
          CockBesideCamera = true, CockPoint = new Vector3(0.34f, -0.36f, 0.24f), CockOffSide = new Vector3(-0.04f, -0.42f, 0.22f), CockAbove = new Vector3(0.26f, 0.44f, 0.22f),
          ThrustCock = new Vector3(0.28f, -0.26f, 0.14f), ThrustEnd = new Vector3(0.06f, -0.17f, 0.60f) };

        /// <summary>A body seen from outside: in front of the chest, within the arm's reach, drawn back beside the shoulder.</summary>
        public static readonly Frame Body = new Frame
        { Centre = new Vector3(0.10f, -0.36f, 0.40f), HalfLength = 0.36f, HalfHeight = 0.34f, Bulge = 0.14f, CockDepth = 0.42f, CockRise = 0.24f, CockOut = 0.12f, ThroughDepth = 0.22f,
          ThrustCock = new Vector3(0.25f, -0.45f, 0.10f), ThrustEnd = new Vector3(0.10f, -0.27f, 0.72f), ThrustDrop = 0.30f };

        /// <summary>A body with both hands on the handle: closer to the middle so the second hand keeps its grip.</summary>
        public static readonly Frame BodyTwoHanded = new Frame
        { Centre = new Vector3(0.04f, -0.38f, 0.42f), HalfLength = 0.28f, HalfHeight = 0.30f, Bulge = 0.12f, CockDepth = 0.30f, CockRise = 0.26f, CockOut = 0.16f, ThroughDepth = 0.14f,
          ThrustCock = new Vector3(0.22f, -0.50f, 0.06f), ThrustEnd = new Vector3(0.04f, -0.32f, 0.62f), ThrustDrop = 0.34f,
          // The same attacks as the player sees of their own (thrust from low in the middle, cuts from the shoulder), a little lower.
          BothHandsInView = true, BothHandsOffset = new Vector3(0f, -0.14f, 0.02f) };

        public static Frame For(bool body, bool twoHanded) =>
            body ? (twoHanded ? BodyTwoHanded : Body) : (twoHanded ? FirstPersonTwoHanded : FirstPerson);

        /// <summary>Path parameter reached at the deepest point of the wind-up (0 = strike begins) and gained after the strike (1 = strike ends).</summary>
        public const float CockBack = 0.75f, FollowThrough = 0.5f;
        /// <summary>Fraction of the wind-up spent drawing back (the rest throws the hand to the strike's start), and of the recovery spent following through.</summary>
        public const float CockAt = 0.6f, ThroughPart = 0.4f;
        /// <summary>Fraction of the wind-up over which the hand leaves the ready pose for the path.</summary>
        public const float TakeUp = 0.35f;
        /// <summary>A blade that turns less than this across the view (aim frame XY of its direction) is a thrust.</summary>
        public const float MinSweep = 0.35f;
        /// <summary>First person: the part of the strike after which the blade swings through from behind the hand.</summary>
        public const float SwingThroughFrom = 0.5f;
        /// <summary>A thrust: how far along the way out the hand already is when the strike begins, and how far ahead the point aims (m).</summary>
        public const float ThrustStart = 0.22f, ThrustTargetAt = 2.2f;

        /// <summary>
        /// The shown hand position and blade direction <paramref name="time"/> seconds into a swing. <paramref name="travel"/> is the
        /// cut's direction across the view (unit, XY). <paramref name="weight"/> is how much of the motion applies: it rises out of
        /// the ready pose at the start and falls back into it at the end. For a thrust <paramref name="travel"/> is zero.
        /// </summary>
        public static bool TrySample(SwingPatternData pattern, in SwingTiming timing, float time, in Frame frame,
            out Vector3 hand, out Vector3 blade, out Vector3 travel, out float weight, bool chained = false)
        {
            hand = blade = travel = default;
            weight = 0f;
            if (pattern == null) return false;
            Vector3 from = pattern.Evaluate(timing.Windup, timing).LocalRotation * Vector3.forward;
            Vector3 to = pattern.Evaluate(timing.ActiveEnd, timing).LocalRotation * Vector3.forward;
            var across = new Vector2(to.x - from.x, to.y - from.y);
            bool thrust = across.magnitude < MinSweep;
            if (!thrust) { across.Normalize(); travel = new Vector3(across.x, across.y, 0f); }

            float u;
            if (time < timing.Windup)
            {
                float k = timing.Windup > 0f ? Mathf.Clamp01(time / timing.Windup) : 1f;
                float thrown = (k - CockAt) / (1f - CockAt);
                u = k < CockAt ? -CockBack * Mathf.SmoothStep(0f, 1f, k / CockAt) : Mathf.Lerp(-CockBack, 0f, thrown * thrown);
                weight = Mathf.SmoothStep(0f, 1f, k / TakeUp);
            }
            else if (time <= timing.ActiveEnd)
            {
                u = timing.Active > 0f ? Mathf.Clamp01((time - timing.Windup) / timing.Active) : 1f;
                weight = 1f;
            }
            else
            {
                float r = timing.Recovery > 0f ? Mathf.Clamp01((time - timing.ActiveEnd) / timing.Recovery) : 1f;
                float through = Mathf.Clamp01(r / ThroughPart);
                u = 1f + FollowThrough * (1f - (1f - through) * (1f - through));
                weight = 1f - Mathf.SmoothStep(0f, 1f, (r - ThroughPart) / (1f - ThroughPart));
            }

            float inside = Mathf.Clamp01(u);
            float before = Mathf.Max(0f, -u) / CockBack;
            float after = Mathf.Max(0f, u - 1f) / FollowThrough;
            if (frame.BothHandsInView)
            {
                // The player's own view never blends to the ready pose here (the caller chains the attacks); a body does.
                if (frame.CockBesideCamera) weight = 1f;
                SampleBothHands(pattern, timing, time, thrust, travel, inside, after, out hand, out blade);
                hand += frame.BothHandsOffset;
                return true;
            }
            if (thrust)
            {
                if (frame.CockBesideCamera)
                {
                    // The player's own view: drawn back at once and held there (the caller blends out of the previous pose),
                    // then thrown forward; no return to the ready pose here.
                    weight = 1f;
                    float k = timing.Windup > 0f ? Mathf.Clamp01(time / timing.Windup) : 1f;
                    float thrown = Mathf.Clamp01((k - CockAt) / (1f - CockAt));
                    if (time < timing.Windup) before = 1f - thrown * thrown;
                }
                // A thrust: the hand is drawn back beside the body with the point on the target, driven straight out along the
                // blade, then pulled back. The point stays on the line the pattern thrusts along.
                Vector3 line = (pattern.Evaluate(timing.Windup + timing.Active * 0.5f, timing).LocalRotation * Vector3.forward).normalized;
                Vector3 end = frame.ThrustEnd + new Vector3(line.x, line.y, 0f) * 0.30f;
                Vector3 start = Vector3.Lerp(frame.ThrustCock, end, ThrustStart);
                hand = u < 0f ? Vector3.Lerp(start, frame.ThrustCock, Mathf.SmoothStep(0f, 1f, before))
                    : Vector3.Lerp(start, end, 1f - (1f - inside) * (1f - inside));
                hand = Vector3.Lerp(hand, start, after * 0.7f);
                Vector3 target = line * ThrustTargetAt + Vector3.down * frame.ThrustDrop;
                blade = (target - hand + Vector3.up * (0.25f * before)).normalized;
                return true;
            }
            if (frame.CockBesideCamera)
            {
                // The player's own view, after the reference recording read frame by frame (first attack 3.9-4.6 s):
                //  - wind-up: the hand goes out of the picture (up beside the head; above for a downward cut; low for a flat cut
                //    from the off side), the blade laid over with its point towards the middle of the screen;
                //  - strike: the hand comes in from that side, crosses no further than the middle and leaves at the bottom, close
                //    to the camera; the blade lies back behind the hand and swings through only at the very end;
                //  - after the strike nothing is left in the picture: the hand is below it, the point down and away.
                // The pose at the end of the strike is held while a combo waits for its next step, so it has to be out of the
                // picture as well. Blending out of the previous pose and back to the ready pose is the caller's (it knows whether
                // another swing follows), so the weight is always 1 here.
                weight = 1f;
                float offSideFlat = Mathf.Max(0f, travel.x) * (1f - Mathf.Abs(travel.y));
                bool downward = travel.y < -0.5f;
                Vector3 centre = frame.Centre + new Vector3(-0.10f, -0.15f, 0f) * offSideFlat;
                Vector3 start = centre - travel * frame.HalfLength + Vector3.up * (0.09f * (1f - offSideFlat));
                // A downward cut starts above the weapon side even when it drifts across: the arm must not lie across the view.
                if (downward) start.x = Mathf.Max(start.x, centre.x - 0.03f);
                // The hand itself stays in the picture at the start (higher up only the forearm would be seen).
                start.y = Mathf.Min(start.y, centre.y + 0.12f);
                // A flat cut from the off side stays along the bottom edge instead of dropping out at once.
                Vector3 end = centre + new Vector3(travel.x * 1.6f, travel.y * 0.8f, 0f) * frame.HalfLength + Vector3.down * (0.30f - 0.18f * offSideFlat);
                // Up beside the head on the weapon side also for a downward cut: from straight above the arm passes in front of the camera.
                Vector3 wait = travel.x > 0.3f && !downward ? frame.CockOffSide : frame.CockPoint;

                Vector3 laid = (downward ? new Vector3(-0.6f, 0.5f, -0.3f) : new Vector3(travel.x * 0.9f, 0.35f, -0.2f)).normalized;
                Vector3 back = (-travel + Vector3.up * 0.45f + Vector3.forward * 0.15f).normalized;
                Vector3 through = (travel * 0.6f + Vector3.down * 0.5f + Vector3.forward * 0.6f).normalized;
                // Inside a combo nothing is shown between the strikes: the blade stays behind the hand instead of being laid over.
                if (chained) laid = back;
                if (time < timing.Windup)
                {
                    float k = timing.Windup > 0f ? Mathf.Clamp01(time / timing.Windup) : 1f;
                    float thrown = Mathf.Clamp01((k - CockAt) / (1f - CockAt));
                    hand = Vector3.Lerp(wait, start, thrown * thrown);
                    blade = Vector3.Slerp(laid, back, Mathf.SmoothStep(0f, 1f, thrown * 1.6f)).normalized;
                }
                else
                {
                    // Across at an even pace, sinking late: the hand reaches the middle before it drops out of the picture.
                    hand = Vector3.Lerp(start, end, inside) + Vector3.forward * (frame.Bulge * Mathf.Sin(Mathf.PI * inside));
                    hand.y = Mathf.Lerp(start.y, end.y, Mathf.Pow(inside, 1.6f));
                    hand += (travel * 0.10f + Vector3.down * 0.20f) * after;
                    blade = Vector3.Slerp(back, through, Mathf.SmoothStep(0f, 1f, (inside - SwingThroughFrom) / (1f - SwingThroughFrom))).normalized;
                }
                return true;
            }
            // Outside the strike the hand does not keep running along the line: it turns back towards the body.
            float along = u < 0f ? u * 0.35f : u > 1f ? 1f + (u - 1f) * 0.6f : u;
            float half = Mathf.Lerp(frame.HalfLength, frame.HalfHeight, travel.y * travel.y);
            hand = frame.Centre + travel * (half * (2f * along - 1f)) + Vector3.forward * (frame.Bulge * Mathf.Sin(Mathf.PI * inside));
            if (frame.CockBesideCamera)
            {
                // From the weapon side or from above the arm waits beside the camera; from the off side it waits low across the body.
                bool offSide = travel.x > 0.3f;
                Vector3 wait = offSide ? frame.CockOffSide
                    : frame.CockPoint + Vector3.up * (0.18f * Mathf.Max(0f, -travel.y));
                hand = Vector3.Lerp(hand, wait, Mathf.SmoothStep(0f, 1f, before));
            }
            else hand += (-travel * frame.CockOut + Vector3.back * frame.CockDepth + Vector3.up * frame.CockRise) * before;
            hand += Vector3.back * (frame.ThroughDepth * after);

            // The blade trails the hand: laid back against the travel at the start, swung through along it by the end.
            float lag = u < 0f ? 1f + 0.6f * before : Mathf.Lerp(1f, -0.9f, inside) - 0.4f * after;
            blade = (-travel * lag + Vector3.up * 0.75f + Vector3.forward * (0.2f + 0.6f * inside - 0.5f * before)).normalized;
            return true;
        }

        // ---- A weapon held in both hands, the player's own view ----------------------------------------------------------------
        // After the user's reference recording of 25 Nov 2026 (23:35, a two-handed sword: thrust, cut from the right shoulder to the
        // left, cut down to the left), read at every frame. Unlike the one-handed cut the hands STAY in the picture:
        //  - thrust: the sword sinks low in the middle with its point forward (the hands are below the picture, the front of the
        //    blade is seen at the bottom); the strike pushes both hands up into the bottom of the picture, the blade short and
        //    pointing at the crosshair; then the sword is pulled back to the lower weapon side;
        //  - cut: both fists rise on the weapon side, large, the blade laid back over the shoulder; the blade then sweeps level
        //    across the upper half of the picture and ends pointing to the far side with the arms across the view, where it
        //    stays until the next attack; a descending cut drops out at the lower far side instead, its blade trailing upwards.
        // Positions are the right hand's (aim frame); the second hand follows the handle. Our own motion, no reference asset.
        public static readonly Vector3 BothHandsThrustCock = new Vector3(0.12f, -0.44f, 0.16f), BothHandsThrustEnd = new Vector3(0.04f, -0.24f, 0.52f),
            BothHandsThrustBack = new Vector3(0.30f, -0.42f, 0.22f);
        public static readonly Vector3 BothHandsRaised = new Vector3(0.26f, 0.00f, 0.34f), BothHandsHigh = new Vector3(0.33f, 0.08f, 0.29f),
            BothHandsEnd = new Vector3(-0.24f, -0.06f, 0.42f);
        /// <summary>The point a thrust aims at lies this far ahead; it is aimed this far below the eye line while drawn back and in the strike.</summary>
        public const float BothHandsAimAt = 2.2f, BothHandsDropDrawn = 0.60f, BothHandsDropStrike = 0.15f;

        private static void SampleBothHands(SwingPatternData pattern, in SwingTiming timing, float time, bool thrust, Vector3 travel,
            float inside, float after, out Vector3 hand, out Vector3 blade)
        {
            bool winding = time < timing.Windup;
            float k = timing.Windup > 0f ? Mathf.Clamp01(time / timing.Windup) : 1f;
            if (thrust)
            {
                Vector3 line = (pattern.Evaluate(timing.Windup + timing.Active * 0.5f, timing).LocalRotation * Vector3.forward).normalized;
                hand = winding ? BothHandsThrustCock : Vector3.Lerp(BothHandsThrustCock, BothHandsThrustEnd, 1f - (1f - inside) * (1f - inside));
                hand = Vector3.Lerp(hand, BothHandsThrustBack, Mathf.SmoothStep(0f, 1f, after));
                float drop = winding ? BothHandsDropDrawn : Mathf.Lerp(BothHandsDropDrawn, BothHandsDropStrike, Mathf.SmoothStep(0f, 1f, inside * 2f));
                blade = (line * BothHandsAimAt + Vector3.down * drop - hand).normalized;
                return;
            }

            float side = travel.x > 0.3f ? -1f : 1f;                    // a cut from the off side is the mirror image
            float falling = Mathf.Clamp01(-travel.y * 1.4f);            // 0 = level, 1 = steeply down
            Vector3 raised = new Vector3(BothHandsRaised.x * side, BothHandsRaised.y + 0.08f * falling, BothHandsRaised.z);
            Vector3 high = new Vector3(BothHandsHigh.x * side, BothHandsHigh.y + 0.08f * falling, BothHandsHigh.z);
            Vector3 end = new Vector3(BothHandsEnd.x * side, BothHandsEnd.y - 0.30f * falling, BothHandsEnd.z);
            float yaw, tilt;
            if (winding)
            {
                // Raised beside the shoulder, then lifted a little further back just before the strike.
                hand = Vector3.Lerp(raised, high, Mathf.SmoothStep(0f, 1f, (k - 0.65f) / 0.35f));
                yaw = 115f; tilt = 0.75f;
            }
            else
            {
                // Well out in front on the way across: closer in, the forearms pass right before the camera.
                hand = Vector3.Lerp(high, end, inside) + Vector3.forward * (0.18f * Mathf.Sin(Mathf.PI * inside));
                // A level cut turns the blade right round to the far side; a falling one leaves it trailing upwards behind the hands.
                yaw = Mathf.Lerp(115f, Mathf.Lerp(-75f, 40f, falling), Mathf.SmoothStep(0f, 1f, inside));
                tilt = Mathf.Lerp(0.75f, Mathf.Lerp(0.12f, 0.70f, falling), inside);
                hand += new Vector3(-0.04f * side, -0.05f - 0.30f * falling, 0f) * after;
                // A falling cut ends below the picture; its blade follows it down instead of standing up into the view.
                tilt -= 1.1f * falling * after;
                yaw = Mathf.Lerp(yaw, -60f, falling * after);
            }
            float radians = yaw * Mathf.Deg2Rad;
            blade = new Vector3(Mathf.Sin(radians) * side, tilt, Mathf.Cos(radians)).normalized;
        }

        // ---- The shoulder girdle follows the hands -----------------------------------------------------------------------------
        // An arm does not move from a fixed shoulder. The chest turns towards the side the weapon hand is on and brings the weapon
        // shoulder forward as the hand reaches out; each shoulder (the collar bone) swings a part of the way towards the point its
        // hand is going to. User, 25 Nov 2026: "the shoulders must move too, as anatomically right as possible, for bots as well".
        /// <summary>Part of the hand's bearing the chest takes up, the chest's limit (degrees) and degrees per metre of reach.</summary>
        public const float GirdleYawGain = 0.55f, GirdleYawLimit = 28f, GirdleReachGain = 35f, GirdleNeutralReach = 0.35f;
        /// <summary>Part of the way a collar bone swings towards its hand's target, and its limit (degrees).</summary>
        public const float ClavicleFollow = 0.28f, ClavicleLimit = 22f;

        /// <summary>
        /// How far the chest turns (degrees about the body's up axis, positive = to the right) for the weapon hand at
        /// <paramref name="hand"/> in the aim frame (right-handed weapon side).
        /// </summary>
        public static float GirdleYaw(Vector3 hand)
        {
            float toward = Mathf.Atan2(hand.x, Mathf.Max(0.2f, hand.z)) * Mathf.Rad2Deg * GirdleYawGain;
            float reach = -(hand.z - GirdleNeutralReach) * GirdleReachGain;
            return Mathf.Clamp(toward + reach, -GirdleYawLimit, GirdleYawLimit);
        }

        /// <summary>Swings a collar bone towards the point its hand is going to (within <see cref="ClavicleLimit"/>).</summary>
        public static void SwingClavicle(Transform clavicle, Transform upperArm, Vector3 handTarget, float weight)
        {
            if (clavicle == null || upperArm == null || weight <= 0f) return;
            Vector3 now = upperArm.position - clavicle.position, wanted = handTarget - clavicle.position;
            if (now.sqrMagnitude < 1e-6f || wanted.sqrMagnitude < 1e-6f) return;
            Vector3 axis = Vector3.Cross(now, wanted);
            if (axis.sqrMagnitude < 1e-8f) return;
            float angle = Mathf.Min(Vector3.Angle(now, wanted) * ClavicleFollow, ClavicleLimit) * Mathf.Clamp01(weight);
            clavicle.rotation = Quaternion.AngleAxis(angle, axis.normalized) * clavicle.rotation;
        }

        /// <summary>Weapon rig rotation (blade along forward) for a shown blade direction, the edge towards the cut.</summary>
        public static Quaternion BladeRotation(Vector3 blade, Vector3 travel)
        {
            Vector3 up = Vector3.Cross(blade, travel);
            return up.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(blade, up) : Quaternion.LookRotation(blade);
        }
    }
}
