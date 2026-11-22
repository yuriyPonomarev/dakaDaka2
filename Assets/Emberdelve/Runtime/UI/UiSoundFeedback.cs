using System.Collections;
using Emberdelve.Feedback;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Emberdelve.UI
{
    /// <summary>Button events only; canvas instances perform one deferred scan for existing hand-built buttons.</summary>
    [DisallowMultipleComponent]
    public sealed class UiSoundFeedback : MonoBehaviour, IPointerEnterHandler, ISubmitHandler
    {
        private Button _button;
        private Coroutine _install;
        private int _lastClickFrame = -1;
        private float _nextHover;

        public static void Attach(Button button)
        {
            if (button != null && button.GetComponent<UiSoundFeedback>() == null)
                button.gameObject.AddComponent<UiSoundFeedback>();
        }

        public static void Attach(Canvas canvas)
        {
            if (canvas != null && canvas.GetComponent<UiSoundFeedback>() == null)
                canvas.gameObject.AddComponent<UiSoundFeedback>();
        }

        private void OnEnable()
        {
            _button = GetComponent<Button>();
            if (_button != null) _button.onClick.AddListener(OnClick);
            else if (GetComponent<Canvas>() != null) _install = StartCoroutine(InstallChildren());
        }

        private void OnDisable()
        {
            if (_button != null) _button.onClick.RemoveListener(OnClick);
            if (_install != null) StopCoroutine(_install);
            _install = null;
        }

        private IEnumerator InstallChildren()
        {
            // EnsureCanvas runs before its caller creates the widgets. Scan once after that build finishes.
            yield return null;
            foreach (Button button in GetComponentsInChildren<Button>(true)) Attach(button);
            _install = null;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!Available || Time.unscaledTime < _nextHover) return;
            _nextHover = Time.unscaledTime + .1f;
            WorldAudio.PlayLocal(WorldSound.UiHover, .28f);
        }

        public void OnSubmit(BaseEventData eventData)
        {
            if (Available) OnClick();
        }

        private bool Available => _button != null && _button.isActiveAndEnabled && _button.IsInteractable();

        private void OnClick()
        {
            // Button already validated the click. Its action may disable the panel before this listener runs.
            // Submit also invokes onClick; never emit twice for the same accepted activation.
            if (_lastClickFrame == Time.frameCount) return;
            _lastClickFrame = Time.frameCount;
            WorldAudio.PlayLocal(WorldSound.UiClick, .4f);
        }
    }
}
