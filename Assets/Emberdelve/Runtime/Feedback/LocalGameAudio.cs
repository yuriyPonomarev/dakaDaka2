using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.Feedback
{
    /// <summary>Bounded owner/UI voices, including lobby and paused menus. Imported clips remain shared.</summary>
    public sealed class LocalGameAudio : MonoBehaviour
    {
        private static LocalGameAudio _instance;
        private readonly AudioSource[] _voices = new AudioSource[8];
        private readonly float[] _lastCue = new float[System.Enum.GetValues(typeof(WorldSound)).Length];
        private AudioListener _fallbackListener;
        private float _nextListenerCheck;
        private int _next;
        public static float MasterVolume { get; set; } = .7f;
        public static float UiVolume { get; set; } = .55f;
        public int PlayedCount { get; private set; }

        public static void Play(WorldSound cue, float gain, float pitch)
        {
            if (!Application.isPlaying || Application.isBatchMode) return;
            bool ui = cue >= WorldSound.UiHover && cue <= WorldSound.ItemMove;
            if (!ui && cue != WorldSound.Death && cue != WorldSound.Extract && cue != WorldSound.Fail && Time.timeScale <= 0f) return;
            if (_instance == null)
            {
                var root = new GameObject("LocalGameAudio");
                DontDestroyOnLoad(root);
                _instance = root.AddComponent<LocalGameAudio>();
            }
            _instance.Emit(cue, gain, pitch, ui);
        }

        private void Awake()
        {
            _instance = this;
            for (int i = 0; i < _lastCue.Length; i++) _lastCue[i] = -10f;
            for (int i = 0; i < _voices.Length; i++)
            {
                var voice = new GameObject("LocalVoice" + i); voice.transform.SetParent(transform, false);
                var source = voice.AddComponent<AudioSource>();
                source.playOnAwake = false; source.spatialBlend = 0; source.reverbZoneMix = 0;
                source.ignoreListenerPause = true;
                _voices[i] = source;
            }
            SceneManager.sceneLoaded += SceneLoaded;
            RefreshListener();
        }
        private void SceneLoaded(Scene scene, LoadSceneMode mode) => RefreshListener();
        private void Update()
        {
            if (Time.unscaledTime < _nextListenerCheck) return;
            _nextListenerCheck = Time.unscaledTime + .3f;
            RefreshListener();
        }
        private void RefreshListener()
        {
            bool other = SceneManager.GetActiveScene().name != "Lobby";
            foreach (var listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                if (listener != _fallbackListener && listener.isActiveAndEnabled) { other = true; break; }
            if (!other && _fallbackListener == null) _fallbackListener = gameObject.AddComponent<AudioListener>();
            if (_fallbackListener != null) _fallbackListener.enabled = !other;
        }
        private void Emit(WorldSound cue, float gain, float pitch, bool ui)
        {
            int id = (int)cue;
            if (id < 0 || id >= _lastCue.Length || !float.IsFinite(gain) || !float.IsFinite(pitch)) return;
            float cooldown = cue == WorldSound.UiHover ? .09f : .045f;
            if (Time.unscaledTime - _lastCue[id] < cooldown) return;
            AudioClip clip = GameSoundBank.Next(cue);
            if (clip == null) return;
            _lastCue[id] = Time.unscaledTime;
            int index = _next;
            for (int i = 0; i < _voices.Length; i++)
            { int slot = (_next + i) % _voices.Length; if (!_voices[slot].isPlaying) { index = slot; break; } }
            _next = (index + 1) % _voices.Length;
            AudioSource source = _voices[index];
            source.Stop(); source.clip = clip; source.pitch = Mathf.Clamp(pitch, .8f, 1.2f);
            source.volume = Mathf.Clamp01(gain) * Mathf.Clamp01(MasterVolume) * (ui ? Mathf.Clamp01(UiVolume) : 1f);
            source.Play(); PlayedCount++;
        }
        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            if (_instance == this) _instance = null;
        }
    }
}
