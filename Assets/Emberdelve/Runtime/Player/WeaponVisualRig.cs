using System.Collections;
using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Player
{
    /// <summary>
    /// Temporary first-person weapon visuals made from primitives, plus simple swing/draw motion.
    /// Replace WeaponVisualBuilder with a model-based builder (or a real prefab) later: nothing in the
    /// combat code changes because it only asks for a muzzle position and animation hooks.
    /// </summary>
    public sealed class WeaponVisualRig : MonoBehaviour
    {
        private Transform _muzzle;
        private Transform _arrow;
        private Transform _string;
        private bool _isRanged;

        public Transform Muzzle => _muzzle;
        public bool IsRanged => _isRanged;

        public void Configure(Transform muzzle, Transform arrow, Transform bowString, bool isRanged)
        {
            _muzzle = muzzle;
            _arrow = arrow;
            _string = bowString;
            _isRanged = isRanged;
            if (_arrow != null) _arrow.gameObject.SetActive(false);
        }

        public void PlaySwing(WeaponData weapon)
        {
            if (_isRanged) return;
            if (_swingRoutine != null) StopCoroutine(_swingRoutine);
            _swingRoutine = StartCoroutine(SwingRoutine(weapon));
        }

        private Coroutine _swingRoutine;

        private IEnumerator SwingRoutine(WeaponData weapon)
        {
            float windup = Mathf.Max(0.02f, weapon != null ? weapon.windupTime : 0.2f);
            float active = Mathf.Max(0.02f, weapon != null ? weapon.activeWindow : 0.12f);
            float recovery = Mathf.Max(0.02f, weapon != null ? weapon.recoveryTime : 0.25f);

            Vector3 basePos = transform.localPosition;
            Quaternion baseRot = transform.localRotation;

            float t = 0f;
            while (t < windup)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / windup);
                transform.localRotation = Quaternion.Lerp(baseRot, Quaternion.Euler(-42f, -26f, 34f), k);
                transform.localPosition = basePos + new Vector3(-0.05f, 0.06f, -0.05f) * k;
                yield return null;
            }

            t = 0f;
            while (t < active)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / active);
                transform.localRotation = Quaternion.Lerp(Quaternion.Euler(-42f, -26f, 34f), Quaternion.Euler(34f, 30f, -28f), k);
                transform.localPosition = basePos + new Vector3(-0.05f, 0.06f, -0.05f) * (1f - k) + new Vector3(0.04f, -0.02f, 0.16f) * k;
                yield return null;
            }

            t = 0f;
            while (t < recovery)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / recovery);
                transform.localRotation = Quaternion.Lerp(Quaternion.Euler(34f, 30f, -28f), baseRot, k);
                transform.localPosition = Vector3.Lerp(basePos + new Vector3(0.04f, -0.02f, 0.16f), basePos, k);
                yield return null;
            }

            transform.localRotation = baseRot;
            transform.localPosition = basePos;
            _swingRoutine = null;
        }

        public void SetDrawProgress(float progress)
        {
            if (!_isRanged) return;
            float p = Mathf.Clamp01(progress);

            if (_arrow != null)
            {
                _arrow.gameObject.SetActive(p > 0.02f);
                Vector3 local = _arrow.localPosition;
                local.z = 0.12f - 0.22f * p;
                _arrow.localPosition = local;
            }

            if (_string != null)
            {
                Vector3 local = _string.localPosition;
                local.z = -0.02f - 0.2f * p;
                _string.localPosition = local;
                _string.localScale = new Vector3(0.012f, 0.42f + 0.06f * p, 0.012f);
            }
        }
    }

    public static class WeaponVisualBuilder
    {
        public static WeaponVisualRig Build(WeaponData weapon, Transform parent)
        {
            if (weapon == null || parent == null) return null;

            int ignoreLayer = LayerMask.NameToLayer("Ignore Raycast");
            bool ranged = weapon.IsRanged;

            GameObject root = new GameObject(ranged ? "Weapon_Bow_Blockout" : "Weapon_Sword_Blockout");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = ranged ? new Vector3(0.20f, -0.17f, 0.30f) : new Vector3(0.26f, -0.20f, 0.30f);
            root.transform.localRotation = ranged ? Quaternion.Euler(0f, -8f, 0f) : Quaternion.Euler(-8f, 12f, 14f);
            if (ignoreLayer >= 0) root.layer = ignoreLayer;

            Transform muzzle;
            Transform arrow = null;
            Transform bowString = null;

            if (ranged)
            {
                BuildPart(root.transform, "Bow_Riser", new Vector3(0f, 0f, 0f), new Vector3(0.05f, 0.20f, 0.07f), Quaternion.identity, weapon.gripMaterial);
                BuildPart(root.transform, "Bow_Limb_Upper", new Vector3(0f, 0.19f, 0.04f), new Vector3(0.03f, 0.30f, 0.04f), Quaternion.Euler(28f, 0f, 0f), weapon.shaftMaterial);
                BuildPart(root.transform, "Bow_Limb_Lower", new Vector3(0f, -0.19f, 0.04f), new Vector3(0.03f, 0.30f, 0.04f), Quaternion.Euler(-28f, 0f, 0f), weapon.shaftMaterial);
                bowString = BuildPart(root.transform, "Bow_String", new Vector3(0f, 0f, -0.02f), new Vector3(0.012f, 0.42f, 0.012f), Quaternion.identity, weapon.edgeMaterial);
                arrow = BuildPart(root.transform, "Bow_Arrow", new Vector3(0f, 0f, 0.12f), new Vector3(0.025f, 0.025f, 0.62f), Quaternion.identity, weapon.shaftMaterial);
                muzzle = BuildPart(root.transform, "Bow_Muzzle", new Vector3(0f, 0f, 0.30f), new Vector3(0.001f, 0.001f, 0.001f), Quaternion.identity, null);
            }
            else
            {
                BuildPart(root.transform, "Sword_Blade", new Vector3(0f, 0f, 0.36f), new Vector3(0.055f, 0.018f, 0.72f), Quaternion.identity, weapon.edgeMaterial);
                BuildPart(root.transform, "Sword_Tip", new Vector3(0f, 0f, 0.74f), new Vector3(0.03f, 0.014f, 0.12f), Quaternion.identity, weapon.edgeMaterial);
                BuildPart(root.transform, "Sword_Guard", new Vector3(0f, 0f, 0.02f), new Vector3(0.17f, 0.035f, 0.05f), Quaternion.identity, weapon.gripMaterial);
                BuildPart(root.transform, "Sword_Grip", new Vector3(0f, -0.02f, -0.07f), new Vector3(0.035f, 0.035f, 0.15f), Quaternion.identity, weapon.gripMaterial);
                muzzle = BuildPart(root.transform, "Sword_Muzzle", new Vector3(0f, 0f, 0.8f), new Vector3(0.001f, 0.001f, 0.001f), Quaternion.identity, null);
            }

            WeaponVisualRig rig = root.AddComponent<WeaponVisualRig>();
            rig.Configure(muzzle, arrow, bowString, ranged);
            return rig;
        }

        private static Transform BuildPart(Transform parent, string name, Vector3 localPosition, Vector3 scale, Quaternion rotation, Material material)
        {
            GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = scale;
            part.transform.localRotation = rotation;

            Collider collider = part.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);

            int ignoreLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreLayer >= 0) part.layer = ignoreLayer;

            Renderer renderer = part.GetComponent<Renderer>();
            if (renderer != null && material != null) renderer.material = material;

            return part.transform;
        }
    }
}
