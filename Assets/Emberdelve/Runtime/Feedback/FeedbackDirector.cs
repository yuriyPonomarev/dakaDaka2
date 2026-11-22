using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Player;

namespace Emberdelve.Feedback
{
    /// <summary>
    /// Listens to gameplay facts and turns them into sound and world flashes. It never inspects
    /// combat state to decide what happened, so the same feedback works unchanged once a server
    /// starts raising those facts from replicated state.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FeedbackDirector : MonoBehaviour
    {
        private static readonly Color BlockFlashColor = new Color(0.55f, 0.75f, 1f);

        [Header("Levels")]
        [Range(0f, 1f)] public float masterVolume = 0.7f;
        [Range(0f, 1f)] public float uiVolume = 0.55f;

        [Header("Flash sizes")]
        public float hitFlashSize = 0.16f;
        public float blockFlashSize = 0.22f;

        private AudioSource _source;

        private AudioClip _draw;
        private AudioClip _hurt;
        private AudioClip _pickup;
        private AudioClip _dry;
        private AudioClip _extract;
        private AudioClip _death;

        private PlayerRig _player;
        private MeleeCombat _melee;
        private RangedCombat _ranged;
        private WallStopFeedback _wallFeedback;
        private bool _subscribedToPlayer;

        private void Awake()
        {
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            _source.volume = 1f;
            _source.reverbZoneMix = 0f;
            if (GetComponent<DungeonAmbience>() == null) gameObject.AddComponent<DungeonAmbience>();
            if (GetComponent<WorldAudio>() == null) gameObject.AddComponent<WorldAudio>();

            _draw = ProceduralAudio.BowDraw();
            _hurt = ProceduralAudio.Hurt();
            _pickup = ProceduralAudio.Pickup();
            _dry = ProceduralAudio.DryFire();
            _extract = ProceduralAudio.Extract();
            _death = ProceduralAudio.Death();
        }

        private void OnEnable()
        {
            GameEvents.DamageDealt += OnDamageDealt;
            GameEvents.PlayerDamaged += OnPlayerDamaged;
            GameEvents.GuardContact += OnGuardContact;
            GameEvents.ItemCollected += OnItemCollected;
            GameEvents.AmmoChanged += OnAmmoChanged;
            GameEvents.RunEnded += OnRunEnded;
        }

        private void OnDisable()
        {
            GameEvents.DamageDealt -= OnDamageDealt;
            GameEvents.PlayerDamaged -= OnPlayerDamaged;
            GameEvents.GuardContact -= OnGuardContact;
            GameEvents.ItemCollected -= OnItemCollected;
            GameEvents.AmmoChanged -= OnAmmoChanged;
            GameEvents.RunEnded -= OnRunEnded;
            UnhookPlayer();
        }

        private void Update()
        {
            var worldAudio = GetComponent<WorldAudio>();
            if (worldAudio != null) worldAudio.Volume = masterVolume;
            WorldAudio.BindListener(_player != null && _player.Look != null ? _player.Look.AimTransform : null);
            if (_subscribedToPlayer && _player == null) UnhookPlayer();
            if (_subscribedToPlayer) return;
            if (GameServices.TryGet<RunDirector>(out RunDirector director)) HookPlayer(director.Player);
        }

        private void HookPlayer(PlayerRig player)
        {
            if (player == null || _subscribedToPlayer) return;

            _player = player;
            _melee = player.GetComponent<MeleeCombat>();
            _ranged = player.GetComponent<RangedCombat>();

            if (_melee != null)
            {

                _melee.WallStopped += OnMeleeWallStopped;
            }

            if (_ranged != null)
            {
                _ranged.AttackStarted += OnAttackStarted;
                _ranged.ShotFired += OnShotFired;
            }

            // B2: wall-stop spark and camera kick for the local player's own swings (visual only).
            if (_wallFeedback == null) _wallFeedback = gameObject.AddComponent<WallStopFeedback>();
            _wallFeedback.Configure(player.Look != null ? player.Look.AimTransform : null);

            _subscribedToPlayer = true;
        }

        private void UnhookPlayer()
        {
            if (!_subscribedToPlayer) return;

            if (_melee != null)
            {

                _melee.WallStopped -= OnMeleeWallStopped;
            }

            if (_ranged != null)
            {
                _ranged.AttackStarted -= OnAttackStarted;
                _ranged.ShotFired -= OnShotFired;
            }

            _subscribedToPlayer = false;
        }



        private void OnMeleeWallStopped(MeleeSwingStatus status)
        {
            if (_wallFeedback != null) _wallFeedback.Play(status);
            WorldAudio.Play(WorldSound.Metal, _player != null ? _player.AimOrigin : transform.position, .65f);
        }

        private void OnAttackStarted(AttackIntent intent)
        {
            if (intent.Weapon == null) return;

            if (intent.Weapon.IsRanged) Play(_draw);
        }

        private void OnShotFired()
        {
            if (NetRole.IsClientOnly) return;
            WorldAudio.Play(WorldSound.Bow, _player != null ? _player.AimOrigin : transform.position, .8f);
        }

        private void OnDamageDealt(int playerId, HitFeedback feedback)
        {
            if (!PlayerView.Shows(playerId) || (feedback.Blocked && feedback.Damage == 0f)) return;
            if (feedback.Blocked)
            {

                ImpactFlash.Spawn(feedback.Point, BlockFlashColor, blockFlashSize);
            }
            else
            {

                Color color = feedback.TargetKind == EntityKind.Enemy
                    ? new Color(1f, 0.85f, 0.45f)
                    : new Color(1f, 0.4f, 0.35f);
                ImpactFlash.Spawn(feedback.Point, color, hitFlashSize);
            }
        }

        private void OnPlayerDamaged(int playerId, DamageInfo info)
        {
            if (!float.IsFinite(info.Amount) || info.Amount <= 0f) return;
            // The flash is a world effect anyone can see (a hit on a teammate or a rival); the grunt is the local player's own.
            if (PlayerView.Shows(playerId)) Play(_hurt);
            ImpactFlash.Spawn(info.Point, new Color(1f, 0.3f, 0.25f), info.Blocked ? blockFlashSize : hitFlashSize);
        }

        private void OnGuardContact(int playerId, Vector3 point, bool parried) => PlayGuardContact(point, parried, blockFlashSize);

        public static void PlayGuardContact(Vector3 point, bool parried, float size = .22f)
        {
            WorldAudio.Play(parried ? WorldSound.Parry : WorldSound.Metal, point);
            if (parried) ImpactFlash.SpawnParry(point, size);
            else ImpactFlash.Spawn(point, BlockFlashColor, size);
        }

        private void OnItemCollected(int playerId, ItemData item, int totalCollected)
        {
            if (!PlayerView.Shows(playerId)) return;
            Play(_pickup);
        }

        private void OnAmmoChanged(int playerId, int current, int reserve)
        {
            if (!PlayerView.Shows(playerId)) return;
            if (current == 0) Play(_dry);
        }

        private void OnRunEnded(int playerId, RunResult result)
        {
            if (!PlayerView.Shows(playerId)) return;
            if (result.Outcome == RunState.Extracted) Play(_extract);
            else Play(_death);
        }

        private void OnDestroy()
        {
            foreach (AudioClip clip in new[] { _draw, _hurt, _pickup, _dry, _extract, _death })
                if (clip != null) Destroy(clip);
        }

        private void Play(AudioClip clip)
        {
            if (clip == null || _source == null) return;
            _source.PlayOneShot(clip, masterVolume);
        }
    }
}
