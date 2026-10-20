using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.InputLayer;
using Emberdelve.Items;
using Emberdelve.Movement;
using Emberdelve.Skills;
using Emberdelve.Stats;

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
    public sealed class PlayerRig : MonoBehaviour, ICombatEntity, IDefenseProvider, IStatusTarget, IDamageOutgoingProvider, IDamageFlatWeaponBonusProvider, IArmorRatingProvider
    {
        [Header("Components")]
        [SerializeField] private EmberdelveInput input;
        [SerializeField] private PlayerMovement movement;
        [SerializeField] private PlayerLook look;
        [SerializeField] private HealthComponent health;
        [SerializeField] private ManaComponent mana;
        [SerializeField] private StaminaComponent stamina;
        [SerializeField] private StatusComponent status;
        [SerializeField] private SkillCaster skills;
        [SerializeField] private Inventory inventory;
        [SerializeField] private Equipment equipment;
        [SerializeField] private ClassLoadout loadout;

        private int _combatId;
        private CharacterClassData _class;
        private WeaponData _mainHand;
        private WeaponData _offhand;
        private DefenseProfile _defense;
        private bool _guardHeld;
        private bool _wired;

        private StatsComponent _attributeStats;

        /// <summary>
        /// No stamina regen delay is authored yet because nothing spends stamina in this slice; drain and
        /// its delay are U3. Kept as one named constant so the pool is configured explicitly rather than
        /// by an undocumented default.
        /// </summary>
        public const float StaminaRegenDelay = 0f;

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
        public StaminaComponent Stamina => stamina;
        public StatusComponent Status => status;
        public SkillCaster Skills => skills;
        public Inventory Inventory => inventory;
        public Equipment Equipment => equipment;
        public ClassLoadout Loadout => loadout;
        public CharacterClassData Class => _class;
        public WeaponData MainHandWeapon => _mainHand;
        public WeaponData OffhandWeapon => _offhand != null ? _offhand : (_class != null ? _class.offhandShield : null);

        /// <summary>Passive modifiers resolved from the class kit. Null until the class has been applied.</summary>
        public CharacterStatStack Stats => skills != null ? skills.Stats : null;

        /// <summary>
        /// The live six-attribute / derived-vital owner (Docs/Design/ATTRIBUTES.md). Distinct from
        /// <see cref="Stats"/>, which stays the passive and gear StatModifier stack. Null only before the
        /// rig has been wired.
        /// </summary>
        public StatsComponent AttributeStats => _attributeStats;
        public Vector3 AimOrigin => look != null ? look.AimOrigin : transform.position;
        public Vector3 AimDirection => look != null ? look.AimDirection : transform.forward;
        public Vector3 Facing => transform.forward;

        private void Awake()
        {
            WireRig();
        }

        /// <summary>
        /// Component resolution and event wiring, split out of Awake and idempotent so the same rig can
        /// also be driven directly by an editor tool or a test harness that adds the components itself.
        /// Calling it twice resolves nothing twice: the combat registration and the health/mana event
        /// subscriptions happen exactly once.
        /// </summary>
        public void WireRig()
        {
            if (input == null) input = GetComponent<EmberdelveInput>();
            if (movement == null) movement = GetComponent<PlayerMovement>();
            if (look == null) look = GetComponent<PlayerLook>();
            if (health == null) health = GetComponent<HealthComponent>();
            if (inventory == null) inventory = GetComponent<Inventory>();
            if (loadout == null) loadout = GetComponent<ClassLoadout>();

            // Mana, stamina, status state and the skill kit are created here rather than required in the
            // scene, so a rig built before this milestone still gets them, and a rebuilt scene simply has
            // them already.
            if (mana == null) mana = ManaComponent.Ensure(gameObject);
            if (stamina == null) stamina = StaminaComponent.Ensure(gameObject);
            if (status == null) status = StatusComponent.Ensure(gameObject);
            if (equipment == null) equipment = Equipment.Ensure(gameObject);
            if (skills == null)
            {
                skills = GetComponent<SkillCaster>();
                if (skills == null) skills = gameObject.AddComponent<SkillCaster>();
            }

            if (_attributeStats == null) _attributeStats = new StatsComponent();

            if (_wired) return;

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

            _wired = true;
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
        /// Re-applies the numbers that depend on the resolved stat picture after equipment changes: pool
        /// ceilings, regeneration, the movement speed rule and the defense profile.
        ///
        /// Two rules hold this together. First, the effective scores and derived pools are recomputed from the
        /// pristine class numbers plus every ITEM INSTANCE currently in an equipment slot - each worn piece's
        /// fixed authored attribute bonuses and that instance's own rolled attribute affixes, summed once per
        /// slot (U2B5) - so wearing the same piece twice is the same as wearing it once, and swapping two
        /// different rolls of the same base replaces the bonus set instead of adding to it. Second, only the
        /// CEILINGS are re-applied here: SetMax clamps a current pool down when the ceiling shrinks and pays
        /// out nothing when it grows, so equipping cannot heal, unequipping cannot refill, and neither can
        /// revive a dead pool. The regen clocks are not restarted, so a gear change keeps the delay the run
        /// already had.
        /// </summary>
        public void RefreshEquipmentStats()
        {
            if (_class == null) return;

            WireRig();

            RebuildAttributeStatsFromGear();

            VitalLimits limits = ResolveVitalLimits();

            if (health != null) health.SetMax(limits.MaxHealth);

            if (mana != null)
            {
                mana.SetMax(limits.MaxMana);
                mana.SetRegen(limits.ManaRegen, _class.manaRegenDelay);
            }

            if (stamina != null)
            {
                stamina.SetMax(limits.MaxStamina);
                stamina.SetRegen(limits.StaminaRegen, StaminaRegenDelay);
            }

            if (movement != null) PushMovementStatSources();

            _defense = ResolveDefense(_mainHand);
        }

        /// <summary>
        /// The one place that tells the attribute component what is currently worn, and it always says it in
        /// the instance form: the fixed authored bonuses come from each worn definition and the rolled
        /// ability-score affixes come from each worn instance, so the scores, the derived pools, the movement
        /// term, the weapon scale and the ability scale are all produced from one derivation pass over one
        /// snapshot. No equipment component means nothing is worn, which is the pristine class numbers again.
        /// </summary>
        private void RebuildAttributeStatsFromGear()
        {
            if (_attributeStats == null) return;

            _attributeStats.RebuildFromInstances(equipment != null ? equipment.WornInstances : null);
        }

        /// <summary>
        /// The pool ceilings and rates this rig should be running for the class and gear it has right now.
        /// Attribute-derived pristine bases first, then the legacy passive/gear StatModifier stack applied
        /// on top of those bases exactly once - never both a class number and a derived number.
        /// </summary>
        public VitalLimits ResolveVitalLimits()
        {
            VitalLimits limits = new VitalLimits
            {
                MaxHealth = Mathf.Max(1, _class != null ? _class.maxHealth : 1),
                MaxMana = Mathf.Max(1, _class != null ? _class.EffectiveMaxMana : 1),
                ManaRegen = _class != null ? Mathf.Max(0f, _class.manaRegenPerSecond) : 0f,
                MaxStamina = Mathf.Max(1, _class != null ? _class.maxStamina : 1),
                StaminaRegen = _class != null ? Mathf.Max(0f, _class.staminaRegenPerSecond) : 0f
            };

            if (_class == null) return limits;

            if (_attributeStats != null && _attributeStats.IsReady)
            {
                limits.MaxHealth = _attributeStats.MaxHealth;
                limits.MaxMana = _attributeStats.MaxMana;
                limits.ManaRegen = _attributeStats.ManaRegen;
                limits.MaxStamina = _attributeStats.MaxStamina;
                limits.StaminaRegen = _attributeStats.StaminaRegen;
            }

            CharacterStatStack stack = Stats;
            if (stack != null)
            {
                limits.MaxHealth = Mathf.Max(1, Mathf.RoundToInt(stack.Get(SkillStat.MaxHealth, limits.MaxHealth)));
                limits.MaxMana = Mathf.Max(1, Mathf.RoundToInt(stack.Get(SkillStat.MaxMana, limits.MaxMana)));
                limits.ManaRegen = Mathf.Max(0f, stack.Get(SkillStat.ManaRegen, limits.ManaRegen));
            }

            // Stamina has no StatModifier vocabulary in the legacy stack yet (no passive or gear declares
            // it), so its ceiling is the attribute-derived number alone until U3 gives it a drain rule.
            return limits;
        }

        /// <summary>
        /// Fills the pools once from the current class/gear numbers. This is the run-start reset
        /// ClassLoadout calls when a class is applied; equipment changes never take this path because a
        /// ceiling change is not a refill (see <see cref="RefreshEquipmentStats"/>).
        /// </summary>
        public void InitializeVitals()
        {
            if (_class == null) return;

            VitalLimits limits = ResolveVitalLimits();

            if (health != null) health.Configure(limits.MaxHealth, _class.healthRegenPerSecond, _class.regenDelay);
            if (mana != null) mana.Configure(limits.MaxMana, limits.ManaRegen, _class.manaRegenDelay);
            if (stamina != null) stamina.Configure(limits.MaxStamina, limits.StaminaRegen, StaminaRegenDelay);
        }

        /// <summary>One resolved set of pool ceilings/rates. Plain values, so any consumer can log or assert them.</summary>
        public struct VitalLimits
        {
            public int MaxHealth;
            public int MaxMana;
            public float ManaRegen;
            public int MaxStamina;
            public float StaminaRegen;
        }

        /// <summary>
        /// Hands movement the two sources its speed rule needs, in one place: the legacy passive/gear stack
        /// (multiplier and flat) and the live attribute component (the DEX term). Called on class application
        /// and after every equipment change, so an equip or unequip changes the resolved walking speed on the
        /// very next query without either source being applied twice.
        /// </summary>
        private void PushMovementStatSources()
        {
            if (movement == null) return;

            movement.ApplyStatStack(Stats);
            movement.ApplyAttributeStats(_attributeStats);
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
            if (stamina != null) stamina.SetRegenerationActive(false);
            SetGuardHeld(false);
        }

        private void OnDestroy()
        {
            if (!_wired) return;

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
            WireRig();

            _class = data;
            _mainHand = data != null ? data.primaryWeapon : null;
            _offhand = data != null ? data.offhandShield : null;

            // The pristine class scores and base vitals are captured before anything is configured, so the
            // pools ClassLoadout is about to build are derived from them and equipment can only ever be
            // added on top inside a later derivation. The rebuild that follows is what makes a class applied
            // on top of gear that is still worn (a re-entry, a class swap) fill its pools from pristine class
            // numbers plus that gear once, instead of from the bare class - InitializeVitals runs inside
            // loadout.Apply below, so the snapshot has to be complete before it.
            if (_attributeStats != null) _attributeStats.Initialize(data);
            RebuildAttributeStatsFromGear();

            // ClassLoadout is the single class-application point: it configures vitals, mana, the skill kit
            // and the weapon slots, and equipping the primary weapon calls back into ApplyMainHandWeapon.
            if (loadout != null) loadout.Apply(data, config, this);

            // ClassLoadout handed movement the passive/gear stack; the attribute term comes from this rig,
            // which is the only owner of the live scores. Both are pushed together so movement resolves one
            // speed picture rather than two half-updated ones.
            PushMovementStatSources();

            _defense = ResolveDefense(_mainHand);
        }

        /// <summary>
        /// What is in the off hand. The class asset decides the default; an equipped off-hand item replaces it
        /// for as long as it is worn, and taking the item off puts the class default back.
        /// </summary>
        public void SetOffhandWeapon(WeaponData weapon)
        {
            _offhand = weapon != null ? weapon : (_class != null ? _class.offhandShield : null);
            _defense = ResolveDefense(_mainHand);
        }

        private DefenseProfile ResolveDefense(WeaponData weapon)
        {
            if (_class == null) return DefenseProfile.Empty;

            CharacterStatStack stats = Stats;
            float passiveBlockBonus = stats != null
                ? stats.Bonus(SkillStat.BlockReduction, _class.blockReduction)
                : 0f;

            return DefenseProfile.Resolve(_class, weapon, _offhand != null ? _offhand : _class.offhandShield, passiveBlockBonus);
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
        /// Outgoing damage multipliers the authority is allowed to apply: the attribute rule for the weapon
        /// actually being used, then the permanent passive/gear StatModifier factors, then any temporary
        /// buff. The attacker never multiplies its own damage, and the attribute term is applied here and
        /// nowhere else, so LocalCombatAuthority's single read of this provider is what makes it count once.
        /// </summary>
        public float GetOutgoingDamageMultiplier(WeaponData weapon)
        {
            float multiplier = 1f;
            CharacterStatStack stats = Stats;

            // Docs/Design/ATTRIBUTES.md: STR for sword/axe, DEX for bow/crossbow, the better of the two for
            // a definition marked finesse. Read as a multiplier (base damage 1) so the weapon's own number
            // still comes from the weapon asset inside the resolver.
            if (weapon != null && _attributeStats != null && _attributeStats.IsReady)
            {
                multiplier *= _attributeStats.GetWeaponDamage(1f, weapon.DamageScale);
            }

            if (stats != null && weapon != null)
            {
                multiplier *= weapon.IsRanged
                    ? stats.Multiplier(SkillStat.RangedDamage)
                    : stats.Multiplier(SkillStat.MeleeDamage);
            }

            if (status != null) multiplier *= status.OutgoingDamageMultiplier;

            return Mathf.Max(0f, multiplier);
        }

        /// <summary>
        /// The FLAT weapon-base damage the currently worn item INSTANCES add to the weapon actually being used
        /// (Docs/Design/ITEM_AFFIXES.md <c>melee_damage</c> / <c>projectile_damage</c>). The typed key is chosen
        /// by the weapon's own channel - <see cref="Emberdelve.Data.WeaponData.IsRanged"/> picks projectile_damage,
        /// anything else picks melee_damage - and every worn slot is summed once through that instance's
        /// immutable accepted <see cref="Emberdelve.Items.AffixRecord"/>s via
        /// <see cref="Emberdelve.Items.ItemInstance.AffixTotal"/>. No strings, no
        /// stat mutation, no cache: the combat authority re-reads this at each accepted hit, so equipping,
        /// unequipping, swapping to a different roll of the same base and a repeated refresh all resolve from
        /// the live slot contents and nothing accumulates.
        ///
        /// It is a separate channel from <see cref="GetOutgoingDamageMultiplier"/> on purpose. A rolled damage
        /// affix is NOT mapped onto the legacy SkillStat.MeleeDamage/RangedDamage multipliers, so the authored
        /// fixed multipliers, the STR/DEX attribute factor and any temporary buff keep their exact existing
        /// numbers, while the rolled flat scales the weapon base before falloff/charge instead of scaling the
        /// finished product. A non-finite or negative total sanitizes to 0 rather than multiplying damage by
        /// nonsense, and an entity with no gear - or a weapon whose channel nothing matches - gets 0, which is
        /// the previous behaviour unchanged.
        /// </summary>
        public float GetFlatWeaponDamageBonus(WeaponData weapon)
        {
            if (weapon == null) return 0f;

            IReadOnlyList<ItemInstance> worn = equipment != null ? equipment.WornInstances : null;
            if (worn == null || worn.Count == 0) return 0f;

            AffixStatKey key = weapon.IsRanged ? AffixStatKey.ProjectileDamage : AffixStatKey.MeleeDamage;

            float total = 0f;
            for (int i = 0; i < worn.Count; i++)
            {
                ItemInstance item = worn[i];
                if (item == null) continue;
                total += item.AffixTotal(key);
            }

            return WeaponData.SanitizeBaseDamageBonus(total);
        }

        /// <summary>
        /// The physical ARMOR RATING (Docs/Design/ARMOR_RULES.md, U2B8) this rig is currently wearing, read
        /// live from the one equipment stat source <see cref="Emberdelve.Items.Equipment.RefreshStats"/>
        /// replaces on every equip, unequip, uid swap and repeated refresh: that source already holds each worn
        /// definition's authored <see cref="SkillStat.Armor"/> modifiers AND each worn instance's rolled
        /// armor affix records mapped onto the same key (U2B6/U2B8), so this single flat total is the fixed and
        /// rolled rating summed exactly once - no second pass over the instances, no cache, no accumulation,
        /// and nothing left behind when a slot empties. A class passive that authors Armor lands in the same
        /// stack and is counted once by the same rule.
        ///
        /// It is a rating, not a damage claim and not a guard reduction: the authority reads it from the
        /// defender at the accepted-hit boundary, the intent carries nothing, and the resolver applies it to
        /// Physical damage only, from every direction, after the directional guard. The claim is sanitized to
        /// a nonnegative finite number here as well as in the resolver, so a tampered stack cannot poison
        /// damage; nothing worn and no stat stack at all both mean 0, which is the previous behaviour
        /// unchanged.
        /// </summary>
        public float GetArmorRating()
        {
            CharacterStatStack stack = Stats;
            if (stack == null) return 0f;

            return DamageResolver.SanitizeArmorRating(stack.Flat(SkillStat.Armor));
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
