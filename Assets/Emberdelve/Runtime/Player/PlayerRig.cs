using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.InputLayer;
using Emberdelve.Items;
using Emberdelve.Movement;

namespace Emberdelve.Player
{
    /// <summary>
    /// Composition root for the local player. It owns identity (combat id, kind), exposes aim and
    /// guard state, and forwards class setup. Behaviour lives in the components it references.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EmberdelveInput))]
    [RequireComponent(typeof(PlayerMovement))]
    [RequireComponent(typeof(PlayerLook))]
    [RequireComponent(typeof(HealthComponent))]
    public sealed class PlayerRig : MonoBehaviour, ICombatEntity, IDefenseProvider
    {
        [Header("Components")]
        [SerializeField] private EmberdelveInput input;
        [SerializeField] private PlayerMovement movement;
        [SerializeField] private PlayerLook look;
        [SerializeField] private HealthComponent health;
        [SerializeField] private Inventory inventory;
        [SerializeField] private ClassLoadout loadout;

        private int _combatId;
        private CharacterClassData _class;
        private WeaponData _mainHand;
        private DefenseProfile _defense;
        private bool _guardHeld;

        public EntityKind Kind => EntityKind.LocalPlayer;
        public int CombatId => _combatId;
        public Vector3 CombatCenter => transform.position + Vector3.up * 1.1f;
        public Collider HeadCollider => null;

        public bool IsDead => health != null && health.IsDead;
        public float CurrentHealth => health != null ? health.CurrentHealth : 0f;
        public float MaxHealth => health != null ? health.MaxHealth : 0f;
        public bool IsGuarding => _guardHeld && _defense.CanBlock && !IsDead;
        public bool CanGuard => _defense.CanBlock;
        public bool CanParry => _defense.CanParry;
        public DefenseProfile Defense => _defense;
        public bool IsAlive => !IsDead;

        public EmberdelveInput Input => input;
        public PlayerMovement Movement => movement;
        public PlayerLook Look => look;
        public HealthComponent Health => health;
        public Inventory Inventory => inventory;
        public ClassLoadout Loadout => loadout;
        public CharacterClassData Class => _class;
        public WeaponData MainHandWeapon => _mainHand;
        public Vector3 AimOrigin => look != null ? look.AimOrigin : transform.position;
        public Vector3 AimDirection => look != null ? look.AimDirection : transform.forward;
        public Vector3 Facing => transform.forward;

        private void Awake()
        {
            if (input == null) input = GetComponent<EmberdelveInput>();
            if (movement == null) movement = GetComponent<PlayerMovement>();
            if (look == null) look = GetComponent<PlayerLook>();
            if (health == null) health = GetComponent<HealthComponent>();
            if (inventory == null) inventory = GetComponent<Inventory>();
            if (loadout == null) loadout = GetComponent<ClassLoadout>();

            _combatId = CombatRegistry.AllocateId();
            CombatRegistry.Register(_combatId, this);

            // HealthComponent only knows its own pool; GameEvents is what the HUD listens to. This rig is
            // the local player's composition root, so it is the place that turns local health into the
            // player-health event. Enemies keep their own Changed subscriptions and raise nothing here.
            if (health != null) health.Changed += OnHealthChanged;
        }

        private void OnHealthChanged(float current, float max)
        {
            GameEvents.RaisePlayerHealth(current, max);
        }

        private void OnDestroy()
        {
            if (health != null) health.Changed -= OnHealthChanged;
            CombatRegistry.Unregister(_combatId, this);
        }

        public void ApplyClass(CharacterClassData data, GameConfig config)
        {
            _class = data;
            _mainHand = data != null ? data.primaryWeapon : null;
            _defense = DefenseProfile.Resolve(data, _mainHand, data != null ? data.offhandShield : null);
            if (loadout != null) loadout.Apply(data, config, this);
        }

        /// <summary>
        /// Called by ClassLoadout whenever a different weapon ends up in the main hand. Defensive
        /// capability belongs to the weapon actually held, not to the class as a whole, so switching a
        /// sword for a crossbow legitimately removes the block and switching a bow for a rapier can
        /// legitimately introduce a Parry flag.
        /// </summary>
        public void ApplyMainHandWeapon(WeaponData weapon)
        {
            _mainHand = weapon;
            _defense = DefenseProfile.Resolve(_class, _mainHand, _class != null ? _class.offhandShield : null);
            if (movement != null) movement.SetGuardActive(IsGuarding);
        }

        /// <summary>
        /// Only the combat authority calls this. A player object never applies damage to itself,
        /// which is exactly the property an authoritative server needs.
        /// </summary>
        public void ApplyDamage(DamageInfo damage)
        {
            if (health == null) return;
            health.ApplyDamage(damage);
        }

        public void SetGuardHeld(bool held)
        {
            _guardHeld = held;
            if (movement != null) movement.SetGuardActive(IsGuarding);
        }

        public bool TryGetGuardReduction(in DamageInfo incoming, out float reduction)
        {
            reduction = 0f;
            if (!IsGuarding || !_defense.CanBlock) return false;

            Vector3 toSource = incoming.SourcePosition - transform.position;
            toSource.y = 0f;
            if (toSource.sqrMagnitude < 0.0001f) toSource = -incoming.Direction;
            toSource.Normalize();

            // Direction-aware block: the guard only holds inside the kit's own arc towards the source.
            float angle = Vector3.Angle(transform.forward, toSource);
            if (angle > _defense.BlockArcDegrees * 0.5f) return false;

            reduction = Mathf.Clamp01(_defense.BlockReduction);
            return true;
        }

        public void PlaySwingVisual(WeaponData weapon)
        {
            if (loadout != null) loadout.PlaySwingVisual(weapon);
        }

        public void SetDrawVisual(float progress)
        {
            if (loadout != null) loadout.SetDrawVisual(progress);
        }

        public Vector3 GetMuzzlePosition()
        {
            if (loadout != null && loadout.HasMuzzle) return loadout.MuzzlePosition;
            return AimOrigin + AimDirection * 0.35f;
        }

        public void SetAimingPenalty(float multiplier)
        {
            if (movement != null) movement.SetSpeedPenalty(multiplier);
        }

        public void SetInputEnabled(bool enabled)
        {
            if (input != null) input.SetGameplayInputEnabled(enabled);
        }
    }
}
