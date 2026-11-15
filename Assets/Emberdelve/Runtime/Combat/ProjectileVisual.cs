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

            float length = Mathf.Max(0.2f, weapon.projectileLength);
            GameObject arrow = CreateModel(weapon, length);
            arrow.name = "Arrow_Visual";

            ProjectileVisual visual = arrow.AddComponent<ProjectileVisual>();
            visual.Configure(start, delta.normalized, end, Mathf.Max(2f, weapon.projectileSpeed), length);
            visual._authorityDriven = authorityDriven;
            return visual;
        }

        private static Material _fallbackMaterial;
        private static Mesh _headMesh;

        /// <summary>Shared cosmetic model centered on its length, pointing along local +Z.
        /// Visual thickness is independent of the authority's collision radius.</summary>
        public static GameObject CreateModel(WeaponData weapon, float length)
        {
            var root = new GameObject("ProjectileModel");
            Material shaft = weapon != null ? weapon.shaftMaterial : null;
            Material edge = weapon != null ? weapon.edgeMaterial : null;
            Material feather = weapon != null ? weapon.gripMaterial : null;
            if (shaft == null || edge == null || feather == null)
            {
                if (_fallbackMaterial == null)
                {
                    _fallbackMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    _fallbackMaterial.name = "ProjectileFallback";
                    _fallbackMaterial.SetColor("_BaseColor", new Color(.32f, .25f, .16f));
                }
                if (shaft == null) shaft = _fallbackMaterial;
                if (edge == null) edge = shaft;
                if (feather == null) feather = shaft;
            }
            float head = Mathf.Min(.10f, length * .18f);
            Part(root.transform, "Shaft", new Vector3(0, 0, -head * .5f),
                new Vector3(.012f, .012f, length - head), shaft);
            float tail = -length * .5f + .07f;
            Part(root.transform, "FletchingHorizontal", new Vector3(0, 0, tail),
                new Vector3(.065f, .003f, .105f), feather);
            Part(root.transform, "FletchingVertical", new Vector3(0, 0, tail),
                new Vector3(.003f, .065f, .105f), feather);
            if (_headMesh == null)
            {
                _headMesh = new Mesh { name = "ProjectileBroadhead" };
                _headMesh.vertices = new[] { new Vector3(0,0,.5f), new Vector3(-.5f,0,-.5f),
                    new Vector3(0,.12f,-.5f), new Vector3(.5f,0,-.5f), new Vector3(0,-.12f,-.5f) };
                _headMesh.triangles = new[] { 0,2,1, 0,3,2, 0,4,3, 0,1,4, 1,2,3, 1,3,4 };
                _headMesh.RecalculateNormals();
                _headMesh.RecalculateBounds();
            }
            var tip = new GameObject("Broadhead");
            tip.transform.SetParent(root.transform, false);
            tip.transform.localPosition = new Vector3(0, 0, length * .5f - head * .5f);
            tip.transform.localScale = new Vector3(.045f, .045f, head);
            tip.AddComponent<MeshFilter>().sharedMesh = _headMesh;
            tip.AddComponent<MeshRenderer>().sharedMaterial = edge;
            int ignore = LayerMask.NameToLayer("Ignore Raycast");
            foreach (Transform child in root.GetComponentsInChildren<Transform>())
                if (ignore >= 0) child.gameObject.layer = ignore;
            return root;
        }

        private static void Part(Transform parent, string name, Vector3 position, Vector3 scale, Material material)
        {
            GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            Collider collider = part.GetComponent<Collider>();
            collider.enabled = false;
            if (Application.isPlaying) Object.Destroy(collider); else Object.DestroyImmediate(collider);
            part.GetComponent<Renderer>().sharedMaterial = material;
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
