using UnityEngine;
using Emberdelve.Core;

namespace Emberdelve.Feedback
{
    public enum WorldSound { Swing, Impact, Metal, Step, Bow, Parry, DoorCreak, DoorThud }

    /// <summary>Presentation-only, bounded voice pool. Geometry filters sound, never damage or enemy perception.</summary>
    [DisallowMultipleComponent]
    public sealed class WorldAudio : MonoBehaviour
    {
        private const int VoiceCount = 16;
        private static WorldAudio _instance;
        private readonly AudioSource[] _voices = new AudioSource[VoiceCount];
        private readonly AudioLowPassFilter[] _filters = new AudioLowPassFilter[VoiceCount];
        private readonly float[] _gains = new float[VoiceCount];
        private AudioClip[] _clips;
        private Transform _listener;
        private Emberdelve.Combat.LocalCombatAuthority _authority;
        private int _nextVoice;
        private int _levelMask;
        private float _nextOcclusion;
        public float Volume { get; set; } = .7f;
        public int PlayedCount { get; private set; }

        public static void BindListener(Transform listener)
        {
            if (_instance != null) _instance._listener = listener;
        }

        public static bool CanHear(Vector3 position, float range = 24f) =>
            _instance != null && _instance.isActiveAndEnabled && _instance._listener != null &&
            (position - _instance._listener.position).sqrMagnitude < range * range;

        private void Awake()
        {
            _instance = this;
            _levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            _clips = new[] { ProceduralAudio.SwordSwing(), ProceduralAudio.Impact(), ProceduralAudio.Block(),
                ProceduralAudio.StoneStep(), ProceduralAudio.BowRelease(), ProceduralAudio.Parry(),
                ProceduralAudio.DoorCreak(), ProceduralAudio.DoorThud() };
            for (int i = 0; i < VoiceCount; i++)
            {
                var voice = new GameObject("WorldVoice" + i);
                voice.transform.SetParent(transform, false);
                var source = voice.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                source.dopplerLevel = 0f;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.minDistance = 1.5f;
                source.maxDistance = 24f;
                _voices[i] = source;
                _filters[i] = voice.AddComponent<AudioLowPassFilter>();
                _filters[i].cutoffFrequency = 22000f;
            }
        }

        public static void Play(WorldSound sound, Vector3 position, float gain = 1f, float pitch = 1f)
        {
            if (!CanHear(position) || Time.timeScale <= 0f) return;
            _instance.Emit(sound, position, gain, pitch);
        }

        private void Emit(WorldSound sound, Vector3 position, float gain, float pitch)
        {
            int index = _nextVoice;
            for (int i = 0; i < VoiceCount; i++)
            {
                int candidate = (_nextVoice + i) % VoiceCount;
                if (!_voices[candidate].isPlaying) { index = candidate; break; }
            }
            _nextVoice = (index + 1) % VoiceCount;
            AudioSource source = _voices[index];
            source.Stop();
            source.transform.position = position;
            source.clip = _clips[(int)sound];
            source.pitch = Mathf.Clamp(pitch, .75f, 1.25f);
            source.maxDistance = sound == WorldSound.Step ? 18f : 24f;
            _gains[index] = Mathf.Clamp01(gain);
            ApplyOcclusion(index);
            source.Play();
            PlayedCount++;
        }

        private void Update()
        {
            if (_authority == null && GameServices.TryGet<Emberdelve.Combat.ICombatAuthority>(out var authority) &&
                authority is Emberdelve.Combat.LocalCombatAuthority local)
            {
                _authority = local;
                _authority.HitResolved += OnHit;
            }
            if (_listener == null || Time.unscaledTime < _nextOcclusion) return;
            _nextOcclusion = Time.unscaledTime + .12f;
            for (int i = 0; i < VoiceCount; i++) if (_voices[i].isPlaying) ApplyOcclusion(i);
        }

        private void ApplyOcclusion(int index)
        {
            AudioSource source = _voices[index];
            bool blocked = _listener != null && Physics.Linecast(source.transform.position, _listener.position,
                _levelMask, QueryTriggerInteraction.Ignore);
            source.volume = Mathf.Clamp01(Volume) * _gains[index] * (blocked ? .28f : 1f);
            _filters[index].cutoffFrequency = blocked ? 1100f : 22000f;
        }

        private void OnHit(Emberdelve.Combat.CombatHitReport hit)
        {
            if (hit.Blocked && !hit.GuardBroken && hit.Amount == 0f) return; // FeedbackDirector plays the confirmed guard contact.
            Play(hit.Blocked ? WorldSound.Metal : WorldSound.Impact, hit.Point);
        }

        private void OnDisable()
        {
            if (_authority != null) _authority.HitResolved -= OnHit;
            _authority = null;
            foreach (var voice in _voices) if (voice != null) voice.Stop();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_clips != null) foreach (AudioClip clip in _clips) if (clip != null) Destroy(clip);
        }
    }
}
