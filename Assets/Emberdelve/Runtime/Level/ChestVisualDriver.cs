using UnityEngine;
using Emberdelve.Interaction;

namespace Emberdelve.Level
{
    /// <summary>
    /// V2 presentation only: swings the model's lid open when the chest reports Opened. It reads
    /// <see cref="ChestInteractable.Opened"/> and never writes chest state. The lid is re-parented under a hinge pivot at its
    /// rear-bottom edge (found from the lid's own bounds), because the model's pivot is not the hinge. Same speed and sign as
    /// the blockout lid (150 deg/s, rotating about X so the front edge rises).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ChestVisualDriver : MonoBehaviour
    {
        private const float DegreesPerSecond = 150f;

        [SerializeField] private ChestInteractable chest;
        [SerializeField] private Transform hinge;
        [SerializeField] private float openAngle = 105f;

        private float _angle;
        private bool _preview;

        public Transform Hinge => hinge;
        public float CurrentAngle => _angle;
        public bool HasLid => hinge != null;

        public void Configure(ChestInteractable owner, Transform lid, float frontSign, float angle)
        {
            chest = owner;
            openAngle = Mathf.Max(0f, angle);
            hinge = null;
            if (lid == null) return;

            Renderer[] renderers = lid.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return;

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            // Rear edge: opposite of the front (local +Z * frontSign), at the bottom of the lid.
            Vector3 front = transform.forward * Mathf.Sign(frontSign == 0f ? 1f : frontSign);
            float depth = Mathf.Abs(Vector3.Dot(bounds.extents, new Vector3(Mathf.Abs(front.x), 0f, Mathf.Abs(front.z))));
            Vector3 point = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z) - front * depth;

            GameObject pivot = new GameObject("LidHinge");
            pivot.transform.SetParent(transform, true);
            pivot.transform.SetPositionAndRotation(point, Quaternion.LookRotation(front, Vector3.up));
            lid.SetParent(pivot.transform, true);
            hinge = pivot.transform;
        }

        private void Update()
        {
            if (hinge == null) return;

            bool open = _preview || (chest != null && chest.Opened);
            if (!open || _angle >= openAngle) return;

            _angle = Mathf.Min(openAngle, _angle + Time.deltaTime * DegreesPerSecond);
            hinge.localRotation = Quaternion.Euler(-_angle, 0f, 0f);
        }

        /// <summary>Opens the lid without touching the chest (graphical review).</summary>
        public void PreviewOpen()
        {
            _preview = true;
        }
    }
}
