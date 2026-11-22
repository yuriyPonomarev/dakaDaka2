using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Flow;

namespace Emberdelve.Feedback
{
    /// <summary>Actual overhead geometry distinguishes open ruins from covered rooms, including rotated modules.</summary>
    [DisallowMultipleComponent]
    public sealed class DungeonAmbience : MonoBehaviour
    {
        public const string ClipResource = "Emberdelve/Audio/DarkFantasyAmbience";

        private Emberdelve.Player.PlayerRig _boundPlayer;
        public void BindPlayer(Emberdelve.Player.PlayerRig player) => _boundPlayer = player;
        private AudioSource _ambience;
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
            _ambience = gameObject.AddComponent<AudioSource>();
            _ambience.playOnAwake = false;
            _ambience.loop = true;
            _ambience.clip = Resources.Load<AudioClip>(ClipResource);
            if (_ambience.clip == null) Debug.LogError("[DungeonAmbience] Missing ambient clip: " + ClipResource, this);
            _ambience.volume = 0f;
            _ambience.spatialBlend = 0f;
            _ambience.reverbZoneMix = 0f;
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
            bool hasListener = running && player.Look != null;
            if (hasListener)
            {
                Vector3 eye = player.Look.AimOrigin;
                _room.transform.position = eye;
                if (Time.unscaledTime >= _nextProbe)
                {
                    _nextProbe = Time.unscaledTime + .35f;
                    _covered = Physics.Raycast(eye, Vector3.up, 14f, _mask, QueryTriggerInteraction.Ignore);
                    _room.reverbPreset = _covered ? AudioReverbPreset.StoneCorridor : AudioReverbPreset.Off;
                }
                _target = (_covered ? .29f : .42f) * (_feedback != null ? _feedback.masterVolume : .7f);
                if (_ambience.clip != null && !_ambience.isPlaying) _ambience.Play();
            }
            else
            {
                _target = 0f;
                _room.reverbPreset = AudioReverbPreset.Off;
            }
            _ambience.volume = Mathf.MoveTowards(_ambience.volume, _target, Time.unscaledDeltaTime * .25f);
            if (!hasListener && _ambience.volume == 0f && _ambience.isPlaying) _ambience.Stop();
        }
        private void OnDisable()
        {
            if (_ambience != null)
            {
                _ambience.Stop();
                _ambience.volume = 0f;
            }
            if (_room != null) _room.reverbPreset = AudioReverbPreset.Off;
        }
        private void OnDestroy()
        {
            // The resource clip is shared. Only the components created by this instance are owned here.
            if (_ambience != null) Destroy(_ambience);
            if (_room != null) Destroy(_room.gameObject);
        }
    }
}
