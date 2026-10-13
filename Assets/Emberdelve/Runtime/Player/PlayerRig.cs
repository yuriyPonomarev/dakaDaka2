using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.InputLayer;
using Emberdelve.Items;
using Emberdelve.Movement;
using Emberdelve.Skills;

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
    public sealed class PlayerRig : MonoBehaviour, ICombatEntity, IDefenseProvider, IStatusTarget, IDamageOutgoingProvider
    {
        [Header("Components")]
        [SerializeField] private EmberdelveInput input;
        [SerializeField] private PlayerMovement movement;
        [SerializeField] private PlayerLook look;
        [SerializeField] private HealthComponent health;
        [SerializeField] private ManaComponent mana;
        [SerializeField] private StatusComponent status;
        [SerializeField] private SkillCaster skills;
        [SerializeField] private Inventory inventory;
        [SerializeField] private ClassLoadout loadout;

        private int _combatId;
        private CharacterClassData _class;
        private WeaponData _mainHand;
        private DefenseProfile _defense;
        private bool _guardHeld;

        public EntityKind Kind => EntityKind.LocalPlayer;
        public int CombatId => _combatId;
        public int TeamId => CombatTeams.PlayerTeam;
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
        public ManaComponent Mana => mana;
        public StatusComponent Status => status;
        public SkillCaster Skills => skills;
        public Inventory Inventory => inventory;
        public ClassLoadout Loadout => loadout;
        public CharacterClassData Class => _class;
        public WeaponData MainHandWeapon => _mainHand;

        /// <summary>Passive modifiers resolved from the class kit. Null until the class has been applied.</summary>
        public CharacterStatStack Stats => skills != null ? skills.Stats : null;
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

            // Mana, status state and the skill kit are created here rather than required in the scene, so a
            // rig built before this milestone still gets them, and a rebuilt scene simply has them already.
            if (mana == null) mana = ManaComponent.Ensure(gameObject);
            if (status == null) status = StatusComponent.Ensure(gameObject);
            if (skills == null)
            {
                skills = GetComponent<SkillCaster>();
                if (skills == null) skills = gameObject.AddComponent<SkillCaster>();
            }

            _combatId = CombatRegistry.AllocateId();
            CombatRegistry.Register(_combatId, this);

            // HealthComponent only knows its own pool; GameEvents is what the HUD listens to. This rig is
            // the local player's composition root, so it is the place that turns local health into the
            // player-health event. Enemies keep their own Changed subscriptions and raise nothing here.
            if (health != null)
            {
                health.Changed += OnHealthChanged;
                health.Died += OnPlayerDied;
            }

            if (mana != null) mana.Changed += OnManaChanged;
        }

        private void OnHealthChanged(float current, float max)
        {
            GameEvents.RaisePlayerHealth(current, max);
        }

        private void OnManaChanged(float current, float max)
        {
            GameEvents.RaisePlayerMana(current, max);
        }

        /// <summary>
        /// Death is handled as state, not as damage: the kit is cleared, the dash is cancelled, temporary
        /// statuses come off, and mana stops regenerating. RunDirector still owns the run outcome.
        /// </summary>
        private void OnPlayerDied()
        {
            if (skills != null) skills.HandleDeath();
            if (movement != null) movement.CancelDash();
            if (status != null) status.ClearStatuses();
            SetGuardHeld(false);
        }

        private void OnDestroy()
        {
            if (health != null)
            {
                health.Changed -= OnHealthChanged;
                health.Died -= OnPlayerDied;
            }

            if (mana != null) mana.Changed -= OnManaChanged;

            CombatRegistry.Unregister(_combatId, this);
        }

        public void ApplyClass(CharacterClassData data, GameConfig config)
        {
            _class = data;
            _mainHand = data != null ? data.primaryWeapon : null;

            // ClassLoadout is the single class-application point: it configures vitals, mana, the skill kit
            // and the weapon slots, and equipping the primary weapon calls back into ApplyMainHandWeapon.
            if (loadout != null) loadout.Apply(data, config, this);

            _defense = ResolveDefense(_mainHand);
        }

        private DefenseProfile ResolveDefense(WeaponData weapon)
        {
            if (_class == null) return DefenseProfile.Empty;

            CharacterStatStack stats = Stats;
            float passiveBlockBonus = stats != null
                ? stats.Bonus(SkillStat.BlockReduction, _class.blockReduction)
                : 0f;

            return DefenseProfile.Resolve(_class, weapon, _class.offhandShield, passiveBlockBonus);
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
            _defense = ResolveDefense(weapon);
            if (movement != null) movement.SetGuardActive(IsGuarding);
        }

        /// <summary>
        /// Outgoing damage multipliers the authority is allowed to apply: permanent passive modifiers plus
        /// any temporary buff. The attacker never multiplies its own damage.
        /// </summary>
        public float GetOutgoingDamageMultiplier(WeaponData weapon)
        {
            float multiplier = 1f;
            CharacterStatStack stats = Stats;

            if (stats != null && weapon != null)
            {
                multiplier *= weapon.IsRanged
                    ? stats.Multiplier(SkillStat.RangedDamage)
                    : stats.Multiplier(SkillStat.MeleeDamage);
            }

            if (status != null) multiplier *= status.OutgoingDamageMultiplier;

            return Mathf.Max(0f, multiplier);
        }

        public void ApplyStatus(StatusSpec spec, int sourceCombatId)
        {
            if (status == null || IsDead) return;
            status.ApplyStatus(spec, sourceCombatId);
        }

        public void ClearStatuses()
        {
            if (status == null) return;
            status.ClearStatuses();
        }

        public bool HasStatus(StatusKind kind) => status != null && status.HasStatus(kind);

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
