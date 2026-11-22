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
        public static event Action<int, Vector3, bool> GuardContact;
        public static event Action<int, HitFeedback> DamageDealt;
        public static event Action<int, ItemData, int> ItemCollected;
        public static event Action<int> InventoryChanged;
        /// <summary>R6c2: a player's hotbar bindings changed by an explicit assignment (the stack data did not).</summary>
        public static event Action<int> HotbarChanged;
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

        public static void RaiseRunState(RunState state) => GameEventTransaction.Publish(RunStateChanged, state);
        public static void RaiseObjective(string objective) => GameEventTransaction.Publish(ObjectiveChanged, objective);
        public static void RaisePause(bool paused) => GameEventTransaction.Publish(PauseChanged, paused);

        public static void RaisePlayerRunState(int playerId, RunState state) => GameEventTransaction.Publish(PlayerRunStateChanged, playerId, state);
        public static void RaisePlayerHealth(int playerId, float current, float max) => GameEventTransaction.Publish(PlayerHealthChanged, playerId, current, max);
        public static void RaisePlayerDamaged(int playerId, DamageInfo info) => GameEventTransaction.Publish(PlayerDamaged, playerId, info);
        public static void RaiseGuardContact(int playerId, Vector3 point, bool parried) => GameEventTransaction.Publish(GuardContact, playerId, point, parried);
        public static void RaiseDamageDealt(int playerId, HitFeedback feedback) => GameEventTransaction.Publish(DamageDealt, playerId, feedback);
        public static void RaiseItemCollected(int playerId, ItemData item, int totalCollected) => GameEventTransaction.Publish(ItemCollected, playerId, item, totalCollected);
        public static void RaiseInventoryChanged(int playerId) => GameEventTransaction.Publish(InventoryChanged, playerId);
        public static void RaiseHotbarChanged(int playerId) => GameEventTransaction.Publish(HotbarChanged, playerId);
        public static void RaiseEquipmentChanged(int playerId, ItemData item, bool equipped) => GameEventTransaction.Publish(EquipmentChanged, playerId, item, equipped);
        public static void RaiseAmmo(int playerId, int current, int reserve) => GameEventTransaction.Publish(AmmoChanged, playerId, current, reserve);
        public static void RaiseInteractionPrompt(int playerId, string prompt) => GameEventTransaction.Publish(InteractionPromptChanged, playerId, prompt);
        public static void RaiseInteractionHold(int playerId, float progress) => GameEventTransaction.Publish(InteractionHoldChanged, playerId, progress);
        public static void RaiseStatusLine(int playerId, string status) => GameEventTransaction.Publish(StatusLineChanged, playerId, status);
        public static void RaiseActiveWeapon(int playerId, WeaponData weapon) => GameEventTransaction.Publish(ActiveWeaponChanged, playerId, weapon);
        public static void RaisePlayerMana(int playerId, float current, float max) => GameEventTransaction.Publish(PlayerManaChanged, playerId, current, max);
        public static void RaiseSkillState(int playerId, SkillSnapshot snapshot) => GameEventTransaction.Publish(SkillStateChanged, playerId, snapshot);
        public static void RaiseSkillRejected(int playerId, SkillSnapshot snapshot, string reason) => GameEventTransaction.Publish(SkillCastRejected, playerId, snapshot, reason);
        public static void RaiseStatusChanged(int combatId, Data.StatusKind kind, float duration) => GameEventTransaction.Publish(StatusChanged, combatId, kind, duration);
        public static void RaiseToast(int playerId, string message) => GameEventTransaction.Publish(ToastRaised, playerId, message);
        public static void RaiseRunEnded(int playerId, RunResult result) => GameEventTransaction.Publish(RunEnded, playerId, result);

        /// <summary>A toast for every viewer (no owner): lobby messages and other facts that are not about one player.</summary>
        public static void RaiseToast(string message) => GameEventTransaction.Publish(ToastRaised, NoPlayer, message);
    }

    /// <summary>Defers presentation callbacks until a loadout transaction has committed. Nested hand changes share the outer scope.</summary>
    internal static class GameEventTransaction
    {
        private static int _depth;
        private static bool _flushing;
        private static readonly System.Collections.Generic.Queue<Action> Pending = new System.Collections.Generic.Queue<Action>();

        internal static IDisposable Defer()
        {
            _depth++;
            return new Scope();
        }

        internal static void Publish<T1>(Action<T1> handlers, T1 value1)
        {
            if (handlers == null) return;
            if (_depth == 0 && !_flushing) { handlers(value1); return; }
            foreach (Action<T1> listener in handlers.GetInvocationList())
                Pending.Enqueue(() => listener(value1));
        }

        internal static void Publish<T1, T2>(Action<T1, T2> handlers, T1 value1, T2 value2)
        {
            if (handlers == null) return;
            if (_depth == 0 && !_flushing) { handlers(value1, value2); return; }
            foreach (Action<T1, T2> listener in handlers.GetInvocationList())
                Pending.Enqueue(() => listener(value1, value2));
        }

        internal static void Publish<T1, T2, T3>(Action<T1, T2, T3> handlers, T1 value1, T2 value2, T3 value3)
        {
            if (handlers == null) return;
            if (_depth == 0 && !_flushing) { handlers(value1, value2, value3); return; }
            foreach (Action<T1, T2, T3> listener in handlers.GetInvocationList())
                Pending.Enqueue(() => listener(value1, value2, value3));
        }

        private sealed class Scope : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (--_depth != 0 || _flushing) return;
                _flushing = true;
                try
                {
                    while (Pending.Count > 0)
                    {
                        Action callback = Pending.Dequeue();
                        try { callback(); }
                        catch (Exception exception) { Debug.LogException(exception); }
                    }
                }
                finally { _flushing = false; }
            }
        }
    }

    /// <summary>
    /// R7: which player a viewer (HUD, bag screen, feedback, result screen) is bound to. The locally controlled player is
    /// the one the run director names; with no run director (the lobby) nothing is bound and only
    /// <see cref="GameEvents.NoPlayer"/> events show. A viewer asks <see cref="Shows"/> for every per-player event.
    /// </summary>
    public static class PlayerView
    {
        private static int _networkLocalPlayerId;

        /// <summary>Id of the player this process controls and presents, or <see cref="GameEvents.NoPlayer"/> when none is bound.</summary>
        public static int LocalPlayerId
        {
            get
            {
                if (NetRole.IsClientOnly && _networkLocalPlayerId > 0) return _networkLocalPlayerId;
                return GameServices.TryGet<ILocalPlayerSource>(out ILocalPlayerSource source) ? source.LocalPlayerId : GameEvents.NoPlayer;
            }
        }

        /// <summary>U4B: the owner identity from the server-spawned player object, for network client presentation.</summary>
        public static void BindNetworkLocalPlayer(int playerId)
        {
            if (NetRole.IsClientOnly && playerId > 0) _networkLocalPlayerId = playerId;
        }

        public static void ClearNetworkLocalPlayer(int playerId)
        {
            if (_networkLocalPlayerId == playerId) _networkLocalPlayerId = GameEvents.NoPlayer;
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
