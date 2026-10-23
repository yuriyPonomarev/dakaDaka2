using System;
using UnityEngine;
using Emberdelve.Data;

namespace Emberdelve.Core
{
    /// <summary>
    /// Central gameplay event hub. Systems publish facts here; UI, audio and VFX only ever listen.
    /// A future authoritative network layer can raise the exact same events from replicated
    /// server state, which means presentation code does not change when networking is added.
    /// Subscribers must unsubscribe in OnDisable/OnDestroy.
    ///
    /// R7: an event that is about ONE player carries that player's id as its first argument (health, mana, bag,
    /// equipment, weapon, skills, ammo, interaction prompt/hold, toasts, status line, hits, per-player run state and result).
    /// A viewer shows only the events of the player it is bound to (see <see cref="PlayerView"/>); an event for player B
    /// can never update player A's HUD. Run-wide facts (run state, objective, pause) stay global. Player id
    /// <see cref="NoPlayer"/> (0) means "for everybody" (a lobby toast), and every viewer shows it.
    /// </summary>
    public static class GameEvents
    {
        /// <summary>The owner id of an event that is not about any one player.</summary>
        public const int NoPlayer = 0;

        // ---- run-wide ----
        public static event Action<RunState> RunStateChanged;
        public static event Action<string> ObjectiveChanged;
        public static event Action<bool> PauseChanged;

        // ---- per player (first argument: the player id) ----
        public static event Action<int, RunState> PlayerRunStateChanged;
        public static event Action<int, float, float> PlayerHealthChanged;
        public static event Action<int, DamageInfo> PlayerDamaged;
        public static event Action<int, HitFeedback> DamageDealt;
        public static event Action<int, ItemData, int> ItemCollected;
        public static event Action<int> InventoryChanged;
        public static event Action<int, ItemData, bool> EquipmentChanged;
        public static event Action<int, int, int> AmmoChanged;
        public static event Action<int, string> InteractionPromptChanged;
        public static event Action<int, float> InteractionHoldChanged;
        public static event Action<int, string> StatusLineChanged;
        public static event Action<int, WeaponData> ActiveWeaponChanged;
        public static event Action<int, float, float> PlayerManaChanged;
        public static event Action<int, SkillSnapshot> SkillStateChanged;
        public static event Action<int, SkillSnapshot, string> SkillCastRejected;
        public static event Action<int, string> ToastRaised;
        public static event Action<int, RunResult> RunEnded;

        /// <summary>Keyed by combat id (the body that carries the status), as before.</summary>
        public static event Action<int, Data.StatusKind, float> StatusChanged;

        public static void RaiseRunState(RunState state) => RunStateChanged?.Invoke(state);
        public static void RaiseObjective(string objective) => ObjectiveChanged?.Invoke(objective);
        public static void RaisePause(bool paused) => PauseChanged?.Invoke(paused);

        public static void RaisePlayerRunState(int playerId, RunState state) => PlayerRunStateChanged?.Invoke(playerId, state);
        public static void RaisePlayerHealth(int playerId, float current, float max) => PlayerHealthChanged?.Invoke(playerId, current, max);
        public static void RaisePlayerDamaged(int playerId, DamageInfo info) => PlayerDamaged?.Invoke(playerId, info);
        public static void RaiseDamageDealt(int playerId, HitFeedback feedback) => DamageDealt?.Invoke(playerId, feedback);
        public static void RaiseItemCollected(int playerId, ItemData item, int totalCollected) => ItemCollected?.Invoke(playerId, item, totalCollected);
        public static void RaiseInventoryChanged(int playerId) => InventoryChanged?.Invoke(playerId);
        public static void RaiseEquipmentChanged(int playerId, ItemData item, bool equipped) => EquipmentChanged?.Invoke(playerId, item, equipped);
        public static void RaiseAmmo(int playerId, int current, int reserve) => AmmoChanged?.Invoke(playerId, current, reserve);
        public static void RaiseInteractionPrompt(int playerId, string prompt) => InteractionPromptChanged?.Invoke(playerId, prompt);
        public static void RaiseInteractionHold(int playerId, float progress) => InteractionHoldChanged?.Invoke(playerId, progress);
        public static void RaiseStatusLine(int playerId, string status) => StatusLineChanged?.Invoke(playerId, status);
        public static void RaiseActiveWeapon(int playerId, WeaponData weapon) => ActiveWeaponChanged?.Invoke(playerId, weapon);
        public static void RaisePlayerMana(int playerId, float current, float max) => PlayerManaChanged?.Invoke(playerId, current, max);
        public static void RaiseSkillState(int playerId, SkillSnapshot snapshot) => SkillStateChanged?.Invoke(playerId, snapshot);
        public static void RaiseSkillRejected(int playerId, SkillSnapshot snapshot, string reason) => SkillCastRejected?.Invoke(playerId, snapshot, reason);
        public static void RaiseStatusChanged(int combatId, Data.StatusKind kind, float duration) => StatusChanged?.Invoke(combatId, kind, duration);
        public static void RaiseToast(int playerId, string message) => ToastRaised?.Invoke(playerId, message);
        public static void RaiseRunEnded(int playerId, RunResult result) => RunEnded?.Invoke(playerId, result);

        /// <summary>A toast for every viewer (no owner): lobby messages and other facts that are not about one player.</summary>
        public static void RaiseToast(string message) => ToastRaised?.Invoke(NoPlayer, message);
    }

    /// <summary>
    /// R7: which player a viewer (HUD, bag screen, feedback, result screen) is bound to. The locally controlled player is
    /// the one the run director names; with no run director (the lobby) nothing is bound and only
    /// <see cref="GameEvents.NoPlayer"/> events show. A viewer asks <see cref="Shows"/> for every per-player event.
    /// </summary>
    public static class PlayerView
    {
        /// <summary>Id of the player this process controls and presents, or <see cref="GameEvents.NoPlayer"/> when none is bound.</summary>
        public static int LocalPlayerId
        {
            get
            {
                return GameServices.TryGet<ILocalPlayerSource>(out ILocalPlayerSource source) ? source.LocalPlayerId : GameEvents.NoPlayer;
            }
        }

        /// <summary>True when an event owned by <paramref name="ownerId"/> belongs on the local viewer: its own player, or a global one.</summary>
        public static bool Shows(int ownerId)
        {
            if (ownerId == GameEvents.NoPlayer) return true;
            int local = LocalPlayerId;
            return local != GameEvents.NoPlayer && local == ownerId;
        }
    }

    /// <summary>Implemented by the run director: the id of the locally controlled player.</summary>
    public interface ILocalPlayerSource
    {
        int LocalPlayerId { get; }
    }
}
