using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Purely cosmetic arrow: a primitive that flies from muzzle to the resolved endpoint.
    /// It carries no damage. When real weapon/art prefabs replace it, only this class changes.
    /// D2: an authority-driven arrow has NO ballistics of its own. The combat authority simulates the flight (fixed
    /// steps, gravity, walls) and places the arrow every frame with <see cref="MoveAuthoritative"/>, between the last
    /// two simulated positions, and ends it with <see cref="FinishAuthoritative"/>; the straight-line mover in Update is
    /// only the legacy path for arrows spawned without an authority.
    /// </summary>
    public sealed class ProjectileVisual : MonoBehaviour
    {
        private Vector3 _target;
        private float _speed;
        private float _visualLength;
        private float _stuckFor;
        private bool _authorityDriven;

        public Vector3 TipPosition => transform.position + transform.forward * (_visualLength * 0.5f);

        public static ProjectileVisual SpawnArrow(Vector3 start, Vector3 end, WeaponData weapon, bool authorityDriven = false)
        {
            if (weapon == null) return null;

            Vector3 delta = end - start;
            if (delta.sqrMagnitude < 0.0001f) return null;

            GameObject arrow = GameObject.CreatePrimitive(PrimitiveType.Cube);
            arrow.name = "Arrow_Visual";

            int ignoreLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreLayer >= 0) arrow.layer = ignoreLayer;

            Collider collider = arrow.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            float radius = Mathf.Max(0.02f, weapon.projectileRadius);
            float length = Mathf.Max(0.2f, weapon.projectileLength);
            arrow.transform.localScale = new Vector3(radius, radius, length);

            Renderer renderer = arrow.GetComponent<Renderer>();
            if (renderer != null && weapon.shaftMaterial != null) renderer.material = weapon.shaftMaterial;

            ProjectileVisual visual = arrow.AddComponent<ProjectileVisual>();
            visual.Configure(start, delta.normalized, end, Mathf.Max(2f, weapon.projectileSpeed), length);
            visual._authorityDriven = authorityDriven;
            return visual;
        }

        /// <summary>Places the arrow tip at a point of the authority's simulated flight, pointing along its direction of travel.</summary>
        public void MoveAuthoritative(Vector3 position, Vector3 direction)
        {
            SetTipPose(position, direction);
        }

        public void FinishAuthoritative(Vector3 position)
        {
            SetTipPose(position, transform.forward);
            Destroy(gameObject);
        }

        private void Configure(Vector3 start, Vector3 direction, Vector3 end, float speed, float visualLength)
        {
            _visualLength = visualLength;
            SetTipPose(start, direction);
            _target = end;
            _speed = speed;
        }

        private void SetTipPose(Vector3 tip, Vector3 direction)
        {
            if (direction.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(direction);
            transform.position = tip - transform.forward * (_visualLength * 0.5f);
        }

        private void Update()
        {
            if (_authorityDriven) return;
            Vector3 current = TipPosition;
            Vector3 delta = _target - current;

            if (delta.sqrMagnitude <= 0.0001f)
            {
                _stuckFor += Time.deltaTime;
                if (_stuckFor > 1.2f) Destroy(gameObject);
                return;
            }

            float step = Mathf.Min(delta.magnitude, _speed * Time.deltaTime);
            Vector3 next = current + delta.normalized * step;
            SetTipPose(next, delta.normalized);
        }
    }
}
