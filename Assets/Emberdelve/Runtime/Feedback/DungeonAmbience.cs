using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Flow;

namespace Emberdelve.Feedback
{
    /// <summary>Actual overhead geometry distinguishes open ruins from covered rooms, including rotated modules.</summary>
    [DisallowMultipleComponent]
    public sealed class DungeonAmbience : MonoBehaviour
    {
        private Emberdelve.Player.PlayerRig _boundPlayer;
        public void BindPlayer(Emberdelve.Player.PlayerRig player) => _boundPlayer = player;
        private AudioSource _wind;
        private AudioClip _clip;
        private AudioReverbZone _room;
        private RunDirector _director;
        private FeedbackDirector _feedback;
        private int _mask;
        private float _nextProbe;
        private float _target;
        private bool _covered;
        private void Awake()
        {
            _feedback = GetComponent<FeedbackDirector>();
            _mask = LayerMask.GetMask(GameConfig.LevelLayerName);
            _clip = ProceduralAudio.RuinWind();
            _wind = gameObject.AddComponent<AudioSource>();
            _wind.playOnAwake = false;
            _wind.loop = true;
            _wind.clip = _clip;
            _wind.volume = 0f;
            _wind.spatialBlend = 0f;
            _wind.reverbZoneMix = 0f;
            var zone = new GameObject("ListenerRoomReverb");
            zone.transform.SetParent(transform, false);
            _room = zone.AddComponent<AudioReverbZone>();
            _room.minDistance = 1f;
            _room.maxDistance = 2f;
            _room.reverbPreset = AudioReverbPreset.Off;
        }
        private void Update()
        {
            if (_director == null) GameServices.TryGet<RunDirector>(out _director);
            var player = _boundPlayer != null ? _boundPlayer : _director != null ? _director.Player : null;
            bool running = player != null && !player.IsDead && (_boundPlayer != null ||
                (_director.State == RunState.Running && _director.LocalPlayerState == RunState.Running));
            if (running && player.Look != null)
            {
                Vector3 eye = player.Look.AimOrigin;
                _room.transform.position = eye;
                if (Time.unscaledTime >= _nextProbe)
                {
                    _nextProbe = Time.unscaledTime + .35f;
                    _covered = Physics.Raycast(eye, Vector3.up, 14f, _mask, QueryTriggerInteraction.Ignore);
                    _room.reverbPreset = _covered ? AudioReverbPreset.StoneCorridor : AudioReverbPreset.Off;
                }
                _target = (_covered ? .012f : .055f) * (_feedback != null ? _feedback.masterVolume : .7f);
                if (!_wind.isPlaying) _wind.Play();
            }
            else
            {
                _target = 0f;
                _room.reverbPreset = AudioReverbPreset.Off;
            }
            _wind.volume = Mathf.MoveTowards(_wind.volume, _target, Time.unscaledDeltaTime * .04f);
        }
        private void OnDisable()
        {
            if (_wind != null) _wind.Stop();
            if (_room != null) _room.reverbPreset = AudioReverbPreset.Off;
        }
        private void OnDestroy() { if (_clip != null) Destroy(_clip); }
    }
}
