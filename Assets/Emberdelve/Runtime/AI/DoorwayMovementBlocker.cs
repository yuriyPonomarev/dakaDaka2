using UnityEngine;
using Emberdelve.Interaction;

namespace Emberdelve.AI
{
    /// <summary>Clamps grid-steered enemy movement at closed modular door panels.</summary>
    public static class DoorwayMovementBlocker
    {
        private const float ContactSkin = 0.03f;
        public const float EnemyDoorOpenRange = 1.5f;

        /// <summary>Enemies only open a door while actively pursuing a live target and standing beside it.</summary>
        public static bool ShouldEnemyOpenDoor(bool hasCurrentTarget, float horizontalDoorDistance, float cooldownRemaining) =>
            hasCurrentTarget && horizontalDoorDistance <= EnemyDoorOpenRange && cooldownRemaining <= 0f;

        /// <summary>
        /// Returns the portion of a horizontal movement delta that does not carry the probe sphere through a closed door.
        /// The caller keeps its existing navigation path; open doors and other level colliders are ignored here.
        /// </summary>
        public static Vector3 ClampDelta(Vector3 position, Vector3 delta, float probeHeight, float radius,
            int layerMask, RaycastHit[] hitBuffer, Collider[] overlapBuffer)
        {
            return ClampDelta(position, delta, probeHeight, radius, layerMask, hitBuffer, overlapBuffer, out _);
        }

        /// <summary>Clamps movement and reports the closed doorway that stopped it, if any.</summary>
        public static Vector3 ClampDelta(Vector3 position, Vector3 delta, float probeHeight, float radius,
            int layerMask, RaycastHit[] hitBuffer, Collider[] overlapBuffer, out DungeonDoorInteractable blockingDoor)
        {
            blockingDoor = null;
            float distance = delta.magnitude;
            if (distance <= 0.0001f || radius <= 0f || hitBuffer == null || hitBuffer.Length == 0 ||
                overlapBuffer == null || overlapBuffer.Length == 0) return delta;

            Vector3 direction = delta / distance;
            Vector3 origin = position + Vector3.up * Mathf.Max(0f, probeHeight);

            // Sphere casts do not report colliders already overlapping their origin. Let an actor move out of
            // a door that closed around it, but never deeper through the panel. A centered overlap can leave
            // in the requested direction because that only resolves a pre-existing penetration.
            int overlapCount = Physics.OverlapSphereNonAlloc(origin, radius, overlapBuffer, layerMask,
                QueryTriggerInteraction.Ignore);
            bool canMoveWithinOverlap = false;
            Collider[] allOverlaps = null;
            for (int i = 0; i < overlapCount; i++)
            {
                if (TryClampOverlap(overlapBuffer[i], origin, direction, delta, distance, radius,
                        out Vector3 overlapDelta, out bool allowOverlapMove))
                {
                    blockingDoor = overlapBuffer[i] != null ? overlapBuffer[i].GetComponentInParent<DungeonDoorInteractable>() : null;
                    return overlapDelta;
                }
                canMoveWithinOverlap |= allowOverlapMove;
            }
            if (overlapCount >= overlapBuffer.Length)
            {
                allOverlaps = Physics.OverlapSphere(origin, radius, layerMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < allOverlaps.Length; i++)
                {
                    if (TryClampOverlap(allOverlaps[i], origin, direction, delta, distance, radius,
                            out Vector3 overlapDelta, out bool allowOverlapMove))
                    {
                        blockingDoor = allOverlaps[i] != null ? allOverlaps[i].GetComponentInParent<DungeonDoorInteractable>() : null;
                        return overlapDelta;
                    }
                    canMoveWithinOverlap |= allowOverlapMove;
                }
            }

            int hitCount = Physics.SphereCastNonAlloc(origin, radius, direction, hitBuffer, distance, layerMask,
                QueryTriggerInteraction.Ignore);
            float nearestDoorDistance = distance;
            bool hitClosedDoor = false;

            for (int i = 0; i < hitCount; i++)
            {
                Collider hitCollider = hitBuffer[i].collider;
                if (hitCollider == null) continue;
                if (canMoveWithinOverlap && IsSafeOverlappingHit(hitCollider, origin, delta, radius, overlapBuffer,
                        overlapCount, allOverlaps)) continue;
                DungeonDoorInteractable door = hitCollider.GetComponentInParent<DungeonDoorInteractable>();
                if (door == null || door.IsOpen || !door.BlockerEnabled) continue;
                hitClosedDoor = true;
                if (hitBuffer[i].distance < nearestDoorDistance)
                {
                    nearestDoorDistance = hitBuffer[i].distance;
                    blockingDoor = door;
                }
            }

            // A saturated non-alloc query could omit a nearby door hit. Resolve that uncommon case exactly.
            if (hitCount >= hitBuffer.Length)
            {
                RaycastHit[] allHits = Physics.SphereCastAll(origin, radius, direction, distance, layerMask,
                    QueryTriggerInteraction.Ignore);
                for (int i = 0; i < allHits.Length; i++)
                {
                    if (canMoveWithinOverlap && IsSafeOverlappingHit(allHits[i].collider, origin, delta, radius,
                            overlapBuffer, overlapCount, allOverlaps)) continue;
                    DungeonDoorInteractable door = allHits[i].collider != null
                        ? allHits[i].collider.GetComponentInParent<DungeonDoorInteractable>() : null;
                    if (door == null || door.IsOpen || !door.BlockerEnabled) continue;
                    hitClosedDoor = true;
                    if (allHits[i].distance < nearestDoorDistance)
                    {
                        nearestDoorDistance = allHits[i].distance;
                        blockingDoor = door;
                    }
                }
            }

            if (!hitClosedDoor) return delta;
            return direction * Mathf.Max(0f, nearestDoorDistance - ContactSkin);
        }

        private static bool TryClampOverlap(Collider overlap, Vector3 origin, Vector3 direction, Vector3 delta,
            float distance, float radius, out Vector3 clampedDelta, out bool allowOverlapMove)
        {
            clampedDelta = delta;
            allowOverlapMove = false;
            DungeonDoorInteractable door = overlap != null ? overlap.GetComponentInParent<DungeonDoorInteractable>() : null;
            if (door == null || door.IsOpen || !door.BlockerEnabled) return false;
            Collider panel = door.BlockerCollider;
            if (panel == null) return false;
            Vector3 normal = panel.transform.forward;
            float signedDistance = Vector3.Dot(origin - panel.bounds.center, normal);
            float clearance = GetHalfThickness(panel, normal) + radius + ContactSkin;
            if (Mathf.Abs(signedDistance) > clearance) return false;

            float directionIntoPlane = Vector3.Dot(delta, normal);
            if (Mathf.Abs(signedDistance) > 0.001f && directionIntoPlane * signedDistance < 0f)
            {
                clampedDelta = Vector3.zero;
                return true;
            }

            // Exact-center penetration: allow only enough travel to emerge on the requested side.
            if (Mathf.Abs(signedDistance) <= 0.001f && Mathf.Abs(directionIntoPlane) > 0.0001f)
            {
                clampedDelta = direction * Mathf.Min(distance, clearance);
                return true;
            }
            allowOverlapMove = true;
            return false;
        }

        private static bool IsSafeOverlappingHit(Collider hitCollider, Vector3 origin, Vector3 delta, float radius,
            Collider[] overlapBuffer, int overlapCount, Collider[] allOverlaps)
        {
            bool wasOverlapping = false;
            int count = allOverlaps != null ? allOverlaps.Length : overlapCount;
            Collider[] overlaps = allOverlaps ?? overlapBuffer;
            for (int i = 0; i < count; i++)
                if (overlaps[i] == hitCollider) { wasOverlapping = true; break; }
            if (!wasOverlapping) return false;

            DungeonDoorInteractable door = hitCollider != null ? hitCollider.GetComponentInParent<DungeonDoorInteractable>() : null;
            if (door == null || door.IsOpen || !door.BlockerEnabled || door.BlockerCollider == null) return false;
            Collider panel = door.BlockerCollider;
            Vector3 normal = panel.transform.forward;
            float signedDistance = Vector3.Dot(origin - panel.bounds.center, normal);
            float clearance = GetHalfThickness(panel, normal) + radius + ContactSkin;
            float towardPlane = Vector3.Dot(delta, normal) * signedDistance;
            return Mathf.Abs(signedDistance) <= clearance && (Mathf.Abs(signedDistance) <= 0.001f || towardPlane >= 0f);
        }

        private static float GetHalfThickness(Collider collider, Vector3 planeNormal)
        {
            if (collider is BoxCollider box)
                return Mathf.Abs(Vector3.Dot(box.transform.forward, planeNormal)) * box.size.z *
                       Mathf.Abs(box.transform.lossyScale.z) * 0.5f;
            Bounds bounds = collider.bounds;
            Vector3 extents = bounds.extents;
            return Mathf.Abs(planeNormal.x) * extents.x + Mathf.Abs(planeNormal.y) * extents.y +
                   Mathf.Abs(planeNormal.z) * extents.z;
        }
    }
}
