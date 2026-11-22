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

        private WeaponData _lastWeapon;
        private bool _weaponSeeded;
        private bool _weaponDirty;

        private PlayerRig _player;
        private MeleeCombat _melee;
        private WallStopFeedback _wallFeedback;
        private bool _subscribedToPlayer;

        private void Awake()
        {
            if (GetComponent<DungeonAmbience>() == null) gameObject.AddComponent<DungeonAmbience>();
            if (GetComponent<WorldAudio>() == null) gameObject.AddComponent<WorldAudio>();

        }

        private void OnEnable()
        {
            GameEvents.DamageDealt += OnDamageDealt;
            GameEvents.PlayerDamaged += OnPlayerDamaged;
            GameEvents.GuardContact += OnGuardContact;
            GameEvents.ItemCollected += OnItemCollected;
            GameEvents.ActiveWeaponChanged += OnActiveWeaponChanged;
            GameEvents.RunEnded += OnRunEnded;
            GameEvents.SkillCastRejected += OnSkillRejected;
        }

        private void OnDisable()
        {
            GameEvents.DamageDealt -= OnDamageDealt;
            GameEvents.PlayerDamaged -= OnPlayerDamaged;
            GameEvents.GuardContact -= OnGuardContact;
            GameEvents.ItemCollected -= OnItemCollected;
            GameEvents.ActiveWeaponChanged -= OnActiveWeaponChanged;
            GameEvents.RunEnded -= OnRunEnded;
            GameEvents.SkillCastRejected -= OnSkillRejected;
            UnhookPlayer();
        }

        private void Update()
        {
            LocalGameAudio.MasterVolume = masterVolume;
            LocalGameAudio.UiVolume = uiVolume;
            var worldAudio = GetComponent<WorldAudio>();
            if (worldAudio != null) worldAudio.Volume = masterVolume;
            WorldAudio.BindListener(_player != null && !_player.IsDead && !_player.IsExtracted && _player.Look != null ? _player.Look.AimTransform : null);
            if (_subscribedToPlayer && _player == null) UnhookPlayer();
            if (_subscribedToPlayer) return;
            if (GameServices.TryGet<RunDirector>(out RunDirector director)) HookPlayer(director.Player);
        }

        public void BindPlayer(PlayerRig player)
        {
            if (_player == player && _subscribedToPlayer) return;
            UnhookPlayer();
            HookPlayer(player);
        }

        private void HookPlayer(PlayerRig player)
        {
            if (player == null || _subscribedToPlayer) return;

            _player = player;
            var actionAudio = player.GetComponent<PlayerActionAudio>();
            if (actionAudio == null) actionAudio = player.gameObject.AddComponent<PlayerActionAudio>();
            actionAudio.Bind(player);
            _melee = player.GetComponent<MeleeCombat>();

            if (_melee != null)
            {

                _melee.WallStopped += OnMeleeWallStopped;
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

            _subscribedToPlayer = false;
            _player = null; _melee = null; _weaponSeeded = false;
        }



        private void OnMeleeWallStopped(MeleeSwingStatus status)
        {
            if (_wallFeedback != null) _wallFeedback.Play(status);
            WorldAudio.Play(WorldSound.Metal, _player != null ? _player.AimOrigin : transform.position, .65f);
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
            if (PlayerView.Shows(playerId)) WorldAudio.PlayLocal(WorldSound.Hurt, .65f);
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
            WorldAudio.PlayLocal(item != null && item.kind == ItemKind.Currency ? WorldSound.Coins : WorldSound.Pickup, .6f);
        }

        private void OnActiveWeaponChanged(int playerId, WeaponData weapon)
        {
            if (PlayerView.Shows(playerId)) _weaponDirty = true;
        }

        private void LateUpdate()
        {
            if (!_weaponDirty || _player == null) return;
            _weaponDirty = false;
            // Snapshot restores briefly remove/reapply hands. Compare their final state, not those transient callbacks.
            WeaponData weapon = _player.ActiveWeapon;
            bool changed = _weaponSeeded && _lastWeapon != weapon;
            _weaponSeeded = true; _lastWeapon = weapon;
            if (changed) WorldAudio.PlayLocal(WorldSound.Equip, .5f);
        }

        private void OnSkillRejected(int playerId, SkillSnapshot skill, string reason)
        { if (PlayerView.Shows(playerId)) WorldAudio.PlayLocal(WorldSound.UiError, .5f); }

        private void OnRunEnded(int playerId, RunResult result)
        {
            if (!PlayerView.Shows(playerId)) return;
            if (result.Outcome == RunState.Extracted) WorldAudio.PlayLocal(WorldSound.Extract, .8f);
            else WorldAudio.PlayLocal(result.Outcome == RunState.Dead ? WorldSound.Death : WorldSound.Fail, .8f);
        }

    }
}
