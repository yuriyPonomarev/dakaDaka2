using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.UI
{
    /// <summary>Cosmetic class prefab only, isolated from the live world and other portrait cameras.</summary>
    public sealed class LobbyClassPortrait : MonoBehaviour
    {
        private GameObject _stage;
        private RenderTexture _texture;

        public void Build(CharacterClassData character, int index)
        {
            if (character == null || character.characterVisual == null) return;
            var image = gameObject.AddComponent<UnityEngine.UI.RawImage>();
            image.raycastTarget = false;
            _stage = new GameObject("LobbyPortrait_" + character.classId);
            _stage.transform.position = new Vector3(100 + index * 30, -10000, 0);
            var model = Instantiate(character.characterVisual, _stage.transform);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.Euler(0, 15, 0);
            foreach (var child in model.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 31;
            var cameraObject = new GameObject("PortraitCamera", typeof(Camera));
            cameraObject.transform.SetParent(_stage.transform, false);
            var camera = cameraObject.GetComponent<Camera>();
            camera.cullingMask = 1 << 31; camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.035f, .033f, .028f, 1);
            camera.orthographic = true; camera.orthographicSize = 1.08f;
            camera.nearClipPlane = .01f; camera.farClipPlane = 10;
            var focus = _stage.transform.position + Vector3.up * .9f;
            camera.transform.position = focus + Vector3.forward * 4;
            camera.transform.LookAt(focus);
            _texture = new RenderTexture(240, 360, 24) { name = "LobbyPortrait_" + character.classId };
            camera.targetTexture = _texture; image.texture = _texture;
            var lamp = new GameObject("PortraitKey", typeof(Light));
            lamp.transform.SetParent(_stage.transform, false);
            lamp.transform.localPosition = new Vector3(1, 3, 2); lamp.transform.LookAt(focus);
            var light = lamp.GetComponent<Light>(); light.type = LightType.Spot; light.range = 8;
            light.spotAngle = 85; light.intensity = 16; light.cullingMask = 1 << 31;
            var fill = new GameObject("PortraitFill", typeof(Light));
            fill.transform.SetParent(_stage.transform, false); fill.transform.localPosition = new Vector3(-1, 1.8f, 1.5f);
            var fillLight = fill.GetComponent<Light>(); fillLight.type = LightType.Point;
            fillLight.range = 5; fillLight.intensity = 4; fillLight.color = new Color(.68f, .76f, 1);
            fillLight.cullingMask = 1 << 31;
        }

        private void OnEnable() { if (_stage != null) _stage.SetActive(true); }
        private void OnDisable() { if (_stage != null) _stage.SetActive(false); }
        private void OnDestroy()
        {
            if (_stage != null) Destroy(_stage);
            if (_texture != null) { _texture.Release(); Destroy(_texture); }
        }
    }
}
