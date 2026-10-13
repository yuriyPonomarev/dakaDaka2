using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Combat
{
    /// <summary>
    /// Purely cosmetic arrow: a primitive that flies from muzzle to the resolved endpoint.
    /// It carries no damage. When real weapon/art prefabs replace it, only this class changes.
    /// </summary>
    public sealed class ProjectileVisual : MonoBehaviour
    {
        private Vector3 _target;
        private float _speed;
        private float _stuckFor;

        public static void SpawnArrow(Vector3 start, Vector3 end, WeaponData weapon)
        {
            if (weapon == null) return;

            Vector3 delta = end - start;
            if (delta.sqrMagnitude < 0.0001f) return;

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
            visual.Configure(start, delta.normalized, end, Mathf.Max(2f, weapon.projectileSpeed));
        }

        private void Configure(Vector3 start, Vector3 direction, Vector3 end, float speed)
        {
            transform.position = start;
            transform.rotation = Quaternion.LookRotation(direction);
            _target = end;
            _speed = speed;
        }

        private void Update()
        {
            Vector3 current = transform.position;
            Vector3 delta = _target - current;

            if (delta.sqrMagnitude <= 0.0001f)
            {
                _stuckFor += Time.deltaTime;
                if (_stuckFor > 1.2f) Destroy(gameObject);
                return;
            }

            float step = Mathf.Min(delta.magnitude, _speed * Time.deltaTime);
            Vector3 next = current + delta.normalized * step;
            transform.position = next;
            transform.rotation = Quaternion.LookRotation(delta.normalized);
        }
    }
}
