using UnityEngine;
using Emberdelve.Core;

namespace Emberdelve.Feedback
{
    public enum WorldSound
    {
        // Preserve the original eight identifiers for existing serialized references.
        Swing, Impact, Metal, Step, Bow, Parry, DoorCreak, DoorThud,
        StepWood, StepDirt, Jump, Land, LandHeavy, Cloth, Equip, Draw, Reload, DryFire,
        Hurt, Death, EnemyHurt, EnemyDeath, Pickup, Coins, ChestOpen, ChestClose,
        Lever, SecretStone, Portal, Extract, Fail,
        UiHover, UiClick, UiOpen, UiClose, UiError, ItemMove,
        Potion, Bandage, SkillDash, SkillCry, SkillShot, SkillBuff, ZoneWarning,
        ArrowImpact, ShieldBreak, BluntSwing, BluntImpact, Stab
    }

    /// <summary>Audible reach and room send for one kind of world sound. Gains are amplitude, not decibels.</summary>
    public readonly struct AcousticProfile
    {
        public readonly float NearDistance, FarDistance, RoomSend;
        public readonly bool IsSpatial;

        private AcousticProfile(float near, float far, float roomSend, bool spatial = true)
        { NearDistance = near; FarDistance = far; RoomSend = roomSend; IsSpatial = spatial; }

        public float DistanceGain(float distance)
        {
            if (!float.IsFinite(distance)) return 0f;
            if (!IsSpatial || distance <= NearDistance) return 1f;
            if (distance >= FarDistance) return 0f;
            float span = FarDistance - NearDistance;
            float relative = (distance - NearDistance) / (span * .18f);
            float body = 1f / (1f + relative * relative);
            float fade = Mathf.InverseLerp(NearDistance + span * .65f, FarDistance, distance);
            // Zero slope at both the full-volume boundary and final silence; no linear-volume distant plateau.
            return body * (1f - Mathf.SmoothStep(0f, 1f, fade));
        }

        public static AcousticProfile For(WorldSound sound)
        {
            switch (sound)
            {
                case WorldSound.UiHover: case WorldSound.UiClick: case WorldSound.UiOpen:
                case WorldSound.UiClose: case WorldSound.UiError:
                    return new AcousticProfile(0f, 0f, 0f, false);
                case WorldSound.Cloth: return new AcousticProfile(.5f, 6f, .08f);
                case WorldSound.Equip: case WorldSound.Draw: case WorldSound.DryFire:
                case WorldSound.Potion: case WorldSound.Bandage: case WorldSound.ItemMove:
                case WorldSound.Pickup: case WorldSound.Coins:
                    return new AcousticProfile(.75f, 10f, .12f);
                case WorldSound.Reload: case WorldSound.Jump:
                case WorldSound.Swing: case WorldSound.BluntSwing:
                    return new AcousticProfile(.8f, 14f, .15f);
                case WorldSound.Step: case WorldSound.StepWood: case WorldSound.StepDirt:
                    return new AcousticProfile(1f, 18f, .18f);
                case WorldSound.DoorCreak: case WorldSound.Lever:
                    return new AcousticProfile(1f, 20f, .2f);
                case WorldSound.Hurt: case WorldSound.EnemyHurt: case WorldSound.Land:
                case WorldSound.Bow: case WorldSound.Stab: case WorldSound.ArrowImpact:
                    return new AcousticProfile(1f, 24f, .2f);
                case WorldSound.Metal: case WorldSound.Parry: case WorldSound.ShieldBreak:
                case WorldSound.BluntImpact: case WorldSound.LandHeavy:
                    return new AcousticProfile(1.3f, 32f, .25f);
                case WorldSound.SecretStone: case WorldSound.Portal:
                    return new AcousticProfile(1.5f, 36f, .28f);
                case WorldSound.SkillCry: return new AcousticProfile(1.5f, 44f, .3f);
                default: return new AcousticProfile(1.2f, 28f, .22f);
            }
        }
    }

    /// <summary>Presentation-only, bounded voice pool. Geometry filters sound, never damage or enemy perception.</summary>
    [DisallowMultipleComponent]
    public sealed class WorldAudio : MonoBehaviour
    {
        private const int VoiceCount = 24;
        private static WorldAudio _instance;
        private readonly AudioSource[] _voices = new AudioSource[VoiceCount];
        private readonly AudioLowPassFilter[] _filters = new AudioLowPassFilter[VoiceCount];
        private readonly float[] _gains = new float[VoiceCount];
        private readonly AcousticProfile[] _profiles = new AcousticProfile[VoiceCount];
        private readonly Transform[] _emitters = new Transform[VoiceCount];
        private readonly Vector3[] _positions = new Vector3[VoiceCount];
        private readonly Vector3[] _localOffsets = new Vector3[VoiceCount];
        private readonly float[] _obstruction = new float[VoiceCount];
        private readonly bool[] _blocked = new bool[VoiceCount];
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
            for (int i = 0; i < VoiceCount; i++)
            {
                var voice = new GameObject("WorldVoice" + i);
                voice.transform.SetParent(transform, false);
                var source = voice.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                source.dopplerLevel = 0f;
                source.spread = 0f;
                source.panStereo = 0f;
                // Distance is measurable in source.volume below. Keep Unity panning, without attenuating twice.
                source.rolloffMode = AudioRolloffMode.Custom;
                source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Linear(0f, 1f, 1f, 1f));
                source.minDistance = 1f;
                source.maxDistance = 28f;
                source.reverbZoneMix = .22f;
                _voices[i] = source;
                _filters[i] = voice.AddComponent<AudioLowPassFilter>();
                _filters[i].cutoffFrequency = 22000f;
            }
        }

        public static void Play(WorldSound sound, Vector3 position, float gain = 1f, float pitch = 1f)
        {
            if (!float.IsFinite(gain) || !float.IsFinite(pitch) || !float.IsFinite(position.sqrMagnitude)) return;
            AcousticProfile profile = AcousticProfile.For(sound);
            if (!profile.IsSpatial) { PlayLocal(sound, gain, pitch); return; }
            if (!CanHear(position, profile.FarDistance) || Time.timeScale <= 0f) return;
            _instance.Emit(sound, position, gain, pitch, profile, null, Vector3.zero);
        }

        /// <summary>Follows a moving emitter for the whole clip. localOffset is in the emitter's local space.</summary>
        public static void PlayAttached(WorldSound sound, Transform emitter, Vector3 localOffset, float gain = 1f, float pitch = 1f)
        {
            if (emitter == null || !float.IsFinite(localOffset.sqrMagnitude) || !float.IsFinite(gain) || !float.IsFinite(pitch)) return;
            AcousticProfile profile = AcousticProfile.For(sound);
            if (!profile.IsSpatial) { PlayLocal(sound, gain, pitch); return; }
            if (_instance == null || !_instance.isActiveAndEnabled || _instance._listener == null || Time.timeScale <= 0f) return;
            Vector3 position = emitter.TransformPoint(localOffset);
            if (!float.IsFinite(position.sqrMagnitude)) return;
            // A moving cue may begin outside its audible radius and enter it during the same clip.
            _instance.Emit(sound, position, gain, pitch, profile, emitter, localOffset);
        }

        public static void PlayLocal(WorldSound sound, float gain = 1f, float pitch = 1f) => LocalGameAudio.Play(sound, gain, pitch);

        private void Emit(WorldSound sound, Vector3 position, float gain, float pitch, AcousticProfile profile,
            Transform emitter, Vector3 localOffset)
        {
            AudioClip clip = GameSoundBank.Next(sound);
            if (clip == null) return;
            int index = -1;
            int quietest = _nextVoice;
            for (int i = 0; i < VoiceCount; i++)
            {
                int candidate = (_nextVoice + i) % VoiceCount;
                if (!_voices[candidate].isPlaying) { index = candidate; break; }
                if (_voices[candidate].volume < _voices[quietest].volume) quietest = candidate;
            }
            if (index < 0)
            {
                float incoming = Mathf.Clamp01(Volume) * Mathf.Clamp01(gain) * profile.DistanceGain(Vector3.Distance(position, _listener.position));
                // Do not steal an audible tail to allocate an out-of-range moving cue.
                if (incoming <= _voices[quietest].volume) return;
                index = quietest;
            }
            _nextVoice = (index + 1) % VoiceCount;
            AudioSource source = _voices[index];
            source.Stop();
            source.transform.position = position;
            _positions[index] = position;
            source.clip = clip;
            source.pitch = Mathf.Clamp(pitch, .75f, 1.25f);
            source.minDistance = profile.NearDistance;
            source.maxDistance = profile.FarDistance;
            source.reverbZoneMix = profile.RoomSend;
            _gains[index] = Mathf.Clamp01(gain);
            _profiles[index] = profile;
            _emitters[index] = emitter;
            _localOffsets[index] = localOffset;
            _blocked[index] = IsObstructed(position);
            _obstruction[index] = _blocked[index] ? 1f : 0f;
            ApplyAcoustics(index, 0f, true);
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
                _authority.ProjectileEnded += OnProjectileEnded;
                _authority.ProjectileLaunched += OnProjectileLaunched;
            }
        }

        private void LateUpdate()
        {
            bool sampleObstruction = Time.unscaledTime >= _nextOcclusion;
            if (sampleObstruction) _nextOcclusion = Time.unscaledTime + .08f;
            for (int i = 0; i < VoiceCount; i++)
            {
                AudioSource source = _voices[i];
                if (!source.isPlaying) { _emitters[i] = null; continue; }
                if (_emitters[i] != null)
                {
                    Vector3 position = _emitters[i].TransformPoint(_localOffsets[i]);
                    if (float.IsFinite(position.sqrMagnitude)) _positions[i] = position;
                }
                // The pool may live on a moving network owner. Contact sounds stay at their world event point.
                source.transform.position = _positions[i];
                if (sampleObstruction) _blocked[i] = IsObstructed(source.transform.position);
                ApplyAcoustics(i, Time.unscaledDeltaTime, false);
            }
        }

        private bool IsObstructed(Vector3 position)
        {
            if (_listener == null || _levelMask == 0) return false;
            Vector3 offset = _listener.position - position;
            float distance = offset.magnitude;
            if (distance <= .16f) return false;
            // Trim both ends so an impact placed on a wall does not occlude itself on the listener's side.
            return Physics.Raycast(position + offset / distance * .08f, offset / distance,
                distance - .16f, _levelMask, QueryTriggerInteraction.Ignore);
        }

        private void ApplyAcoustics(int index, float dt, bool immediate)
        {
            AudioSource source = _voices[index];
            float obstructionTarget = _blocked[index] ? 1f : 0f;
            float obstructionBlend = 1f - Mathf.Exp(-dt / (_blocked[index] ? .09f : .18f));
            _obstruction[index] = Mathf.Lerp(_obstruction[index], obstructionTarget, obstructionBlend);
            float distanceGain = _listener != null
                ? _profiles[index].DistanceGain(Vector3.Distance(source.transform.position, _listener.position)) : 0f;
            float target = Mathf.Clamp01(Volume) * _gains[index] * distanceGain * Mathf.Lerp(1f, .24f, _obstruction[index]);
            source.volume = immediate ? target : Mathf.Lerp(source.volume, target, 1f - Mathf.Exp(-dt / .035f));
            _filters[index].cutoffFrequency = Mathf.Exp(Mathf.Lerp(Mathf.Log(22000f), Mathf.Log(1100f), _obstruction[index]));
            // Voices run out naturally even at zero gain; walking back into range can reveal the remaining tail.
        }

        private void OnHit(Emberdelve.Combat.CombatHitReport hit)
        {
            if (hit.Blocked && !hit.GuardBroken && hit.Amount == 0f) return; // FeedbackDirector plays the confirmed guard contact.
            if (hit.Amount <= 0f && !hit.Blocked) return;
            Play(hit.GuardBroken ? WorldSound.ShieldBreak : hit.Blocked ? WorldSound.Metal :
                hit.Source != Emberdelve.Combat.CombatHitSource.Melee ? WorldSound.ArrowImpact : ImpactFor(hit.WeaponId), hit.Point);
        }

        public static WorldSound SwingFor(string weaponId) => ImpactFor(weaponId) == WorldSound.BluntImpact ? WorldSound.BluntSwing : WorldSound.Swing;

        public static WorldSound ImpactFor(string weaponId)
        {
            string id = weaponId ?? "";
            return id.IndexOf("club", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("mace", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("hammer", System.StringComparison.OrdinalIgnoreCase) >= 0 ? WorldSound.BluntImpact :
                id.IndexOf("dagger", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("rapier", System.StringComparison.OrdinalIgnoreCase) >= 0 ? WorldSound.Stab : WorldSound.Impact;
        }
        private void OnProjectileLaunched(Emberdelve.Combat.CombatLaunchReport shot) => Play(WorldSound.Bow, shot.Origin, .8f);

        private void OnProjectileEnded(Emberdelve.Combat.ProjectileEndReport end)
        {
            if (end.Reason == Emberdelve.Combat.ProjectileEndReason.Wall) Play(WorldSound.ArrowImpact, end.ImpactPoint, .55f);
        }

        private void OnDisable()
        {
            if (_authority != null) { _authority.HitResolved -= OnHit; _authority.ProjectileEnded -= OnProjectileEnded; _authority.ProjectileLaunched -= OnProjectileLaunched; }
            _authority = null;
            for (int i = 0; i < VoiceCount; i++)
            {
                if (_voices[i] != null) _voices[i].Stop();
                _emitters[i] = null;
            }
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            foreach (var voice in _voices)
            {
                if (voice == null) continue;
                if (Application.isPlaying) Destroy(voice.gameObject); else DestroyImmediate(voice.gameObject);
            }
        }
    }
}
