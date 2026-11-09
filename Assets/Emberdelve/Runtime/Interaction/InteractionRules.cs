using UnityEngine;
using Emberdelve.Core;

namespace Emberdelve.Interaction
{
    /// <summary>R2: a host that tells interactables how far an interaction may reach (the authority range from the game config).</summary>
    public interface IInteractionRulesSource
    {
        float InteractionReach { get; }
    }

    /// <summary>
    /// Optional authority check for interaction hosts that admit actors to a live run. Test/fake hosts can
    /// omit it and retain the geometry-only behavior; production hosts reject impostors before interactables
    /// roll, open, collect, or extract.
    /// </summary>
    public interface IInteractionAdmissionSource
    {
        bool IsInteractionActorAdmitted(IInteractionActor actor, out string reason);
    }

    /// <summary>
    /// R2: the authority-side checks every interaction runs for ITS actor, whatever the client's prompt said: the actor
    /// exists and is alive, is within reach of the target (closest point of the target's colliders, 3 m by default) and has
    /// a clear line of sight to it (level geometry only). Chests, pickups and the gate evaluate this in CanInteract, which the
    /// interaction also re-runs when a hold completes, so a stale prompt cannot act.
    /// </summary>
    public static class InteractionRules
    {
        public const float DefaultReach = 3f;

        /// <summary>Slack so a point lying on the floor next to the target is not "behind" the floor.</summary>
        private const float LineOfSightSlack = 0.12f;

        public static float ReachOf(object host)
        {
            if (!(host is IInteractionRulesSource source)) return DefaultReach;
            float reach = source.InteractionReach;
            return IsFinite(reach) && reach > 0f ? reach : DefaultReach;
        }

        public static bool Validate(IInteractionActor actor, Component target, float reach, out string reason)
        {
            reason = null;
            if (actor == null)
            {
                reason = "No one is interacting";
                return false;
            }

            if (!actor.IsAlive)
            {
                reason = "The dead cannot interact";
                return false;
            }

            if (target == null)
            {
                reason = "Nothing to interact with";
                return false;
            }

            if (!IsFinite(reach))
            {
                reason = "Invalid interaction reach";
                return false;
            }

            Vector3 origin = actor.InteractionOrigin;
            if (!IsFinite(origin))
            {
                reason = "Invalid interaction origin";
                return false;
            }

            Vector3 point = ClosestPoint(target, origin);
            if (!IsFinite(point))
            {
                reason = "Invalid interaction target point";
                return false;
            }

            float distance = Vector3.Distance(origin, point);
            if (!IsFinite(distance) || distance > Mathf.Max(0.5f, reach))
            {
                reason = "Too far away";
                return false;
            }

            if (TryGetLineOfSightBlocker(actor, target, out _, out _))
            {
                reason = "No line of sight";
                return false;
            }

            return true;
        }

        /// <summary>Validates host admission first when the host owns an admission boundary, then geometry.</summary>
        public static bool Validate(IInteractionActor actor, Component target, float reach, object host, out string reason)
        {
            if (host is IInteractionAdmissionSource admission
                && !admission.IsInteractionActorAdmitted(actor, out reason)) return false;
            return Validate(actor, target, reach, out reason);
        }

        /// <summary>Returns the exact level collider that makes a valid target ray fail; used by dev proof diagnostics.</summary>
        public static bool TryGetLineOfSightBlocker(IInteractionActor actor, Component target, out RaycastHit hit, out Vector3 targetPoint)
        {
            hit = default;
            targetPoint = Vector3.zero;
            if (actor == null || target == null) return false;
            Vector3 origin = actor.InteractionOrigin;
            if (!IsFinite(origin)) return false;
            targetPoint = ClosestPoint(target, origin);
            if (!IsFinite(targetPoint)) return false;
            Vector3 offset = targetPoint - origin;
            float distance = offset.magnitude;
            if (!IsFinite(distance) || distance <= LineOfSightSlack * 2f) return false;
            int levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            return levelMask != 0 && Physics.Raycast(origin, offset / distance, out hit,
                distance - LineOfSightSlack, levelMask, QueryTriggerInteraction.Ignore);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static Vector3 ClosestPoint(Component target, Vector3 from)
        {
            Collider[] colliders = target.GetComponentsInChildren<Collider>(false);
            bool found = false;
            Vector3 best = target.transform.position;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider c = colliders[i];
                if (c == null || !c.enabled) continue;
                Vector3 p = c.ClosestPoint(from);
                float sqr = (p - from).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = p;
                    found = true;
                }
            }

            return found ? best : target.transform.position;
        }
    }
}
