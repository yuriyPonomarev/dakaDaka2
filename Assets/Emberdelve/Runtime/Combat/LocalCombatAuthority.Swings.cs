using System.Collections.Generic;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Stats;
using Emberdelve.Player;

namespace Emberdelve.Combat
{
    /// <summary>
    /// D1a: the swept-weapon melee. The authority owns the whole swing.
    ///
    /// Begin (<see cref="TryBeginMeleeSwing"/>): validation only, on the authority's own state and clock.
    /// Step (<see cref="StepSwings"/>, every FixedUpdate, or by hand in a gate): for every running swing the
    /// ACTIVE phase is cut into K equal time samples (K comes from the pattern data alone, see
    /// <see cref="SwingGeometry.SampleCount"/>). Each sample whose time has been reached is processed in order:
    ///   1. the weapon segment (base, tip) is built from the pattern pose at that sample time in the attacker's
    ///      authoritative aim frame (position/yaw/pitch interpolated between this tick's pose and the last one);
    ///   2. level geometry is tested along that segment: a hit clips the segment at the wall and the swing STOPS
    ///      after this sample (recoil) - nothing behind the wall is touched, anything touched earlier keeps its
    ///      damage;
    ///   3. every hostile, live, not-yet-hit entity that exposes hit volumes is tested (segment-vs-capsule with
    ///      the pattern's sweep radius added); the first touch opens a short contact window, the deepest
    ///      volume touched inside the window decides the location, then the damage is applied through the
    ///      ordinary DamageResolver (guard, armour, multipliers) with the location table.
    /// Because the sample grid is fixed by the data, the result does not depend on the tick length: stepping in
    /// 2 ms or 500 ms slices yields the same hits, locations and wall stop.
    /// </summary>
    ///
    // D1b adds the combat FLOW on top of the sweep, all of it authority state per instigator: the combo chain
    // (the next step is the authority's expected step, accepted only inside the combo window of the previous step's
    // recovery), impact zones (the contact parameter along the segment comes from the sweep), hit slow and guard
    // break (statuses and the stagger come from weapon data, never from an intent) and the move-speed factor of the
    // swing phase (ICombatFlowState).
    public sealed partial class LocalCombatAuthority : ICombatFlowState, IComboStateSource
    {
        [Header("Swept melee (D1a)")]
        [Tooltip("Largest angle (degrees) between the aim a client claims when it begins a swing and the pose the authority holds.")]
        [SerializeField] private float swingAimTolerance = 12f;
        [Tooltip("Entities whose centre is further than effective reach + this margin from the eye are skipped before their volumes are tested.")]
        [SerializeField] private float swingBroadPhaseMargin = 3f;

        private const int FinishedSwingMemory = 96;

        private sealed class Contact
        {
            public ICombatEntity Target;
            public int FirstSample;
            public int LastWindowSample;
            public HitLocation Location;
            public float Depth;
            public Vector3 Point;
            public float T;
            public bool GripContact;
            public bool GuardBladeContact;
            public bool GuardShieldContact;
            public bool GuardWeaponContact;
            public float EntryDistance;
        }

        /// <summary>The combo chain an instigator is in: authority state, never taken from an intent.</summary>
        private sealed class ChainState
        {
            public WeaponData Weapon;
            public ICombatEntity Instigator;
            public int WeaponEpoch;
            public int NextStep;
            public float WindowEnd;
        }

        private sealed class SwingRecord
        {
            public int Id;
            public int InstigatorId;
            public ICombatEntity Instigator;
            public IAimPoseProvider Actor;
            public WeaponData Weapon;
            public int WeaponEpoch;
            public SwingPatternData Pattern;
            public SwingTiming Timing;
            public float RepeatInterval;
            public float ActionSpeedFactor;
            public float ContactWindow;
            public int ComboStep;
            public float StepMultiplier = 1f;
            public int Sequence;
            public int RiposteStrike;
            public float StartTime;
            public int SampleCount;
            public int NextSample;
            public float BladeLength;
            public float LastTs;
            public Vector3 LastEye;
            public float LastYaw;
            public float LastPitch;
            public float TurnCeiling = float.PositiveInfinity;
            public float TurnClamped;
            // U3C: the trajectory this swing committed to at begin (warning data) and, when the tuning commits the aim, the aim the sweep keeps.
            public BladeWarning Warning;
            public bool CommitAim;
            public float CommitYaw;
            public float CommitPitch;
            public SwingPhase Phase;
            public SwingEndReason EndReason;
            public float EndTs;
            public bool Stopped;
            public float StopTs;
            public int WallSample = -1;
            public Vector3 WallPoint;
            public Vector3 WallNormal;
            public bool HasWallPoint;
            public readonly HashSet<int> Hit = new HashSet<int>();
            public readonly List<Contact> Pending = new List<Contact>(4);
            public readonly List<MeleeSwingHit> Hits = new List<MeleeSwingHit>(4);
            public IReadOnlyList<MeleeSwingHit> HitsView;
        }

        /// <summary>Per instigator: the stamp of its last accepted swing and that swing's complete accepted repeat interval (frozen across regear).</summary>
        private readonly Dictionary<int, KeyValuePair<float, float>> _lastSwingFloor = new Dictionary<int, KeyValuePair<float, float>>();

        private readonly Dictionary<int, ChainState> _chains = new Dictionary<int, ChainState>();
        private readonly List<int> _chainBuffer = new List<int>(4);
        private readonly Dictionary<int, float> _guardBrokenUntil = new Dictionary<int, float>();
        // Reference port: a defender's block reaction and a parried attacker's "deflected" state, both as "until" stamps on this clock.
        private readonly Dictionary<int, float> _blockReactionUntil = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _deflectedUntil = new Dictionary<int, float>();
        private static CombatFlowTuning _defaultFlow;

        private readonly Dictionary<int, SwingRecord> _swingsById = new Dictionary<int, SwingRecord>();
        private readonly Dictionary<int, SwingRecord> _swingByInstigator = new Dictionary<int, SwingRecord>();
        private readonly List<SwingRecord> _activeSwings = new List<SwingRecord>();
        private readonly Queue<int> _finishedSwingIds = new Queue<int>();
        private readonly List<SwingRecord> _stepBuffer = new List<SwingRecord>();
        private readonly List<ICombatEntity> _registryBuffer = new List<ICombatEntity>(32);
        private readonly List<Contact> _sampleContacts = new List<Contact>(8);
        private readonly List<HitShape> _shapeBuffer = new List<HitShape>(16);
        private int _nextSwingId = 1;

        /// <summary>When true (default) FixedUpdate steps the swings. A gate turns it off to step by hand on an injected clock.</summary>
        internal bool AutoStepSwings = true;

        /// <summary>
        /// When false (default) a melee weapon attack from an entity that has a swing pose (the player) must go
        /// through <see cref="TryBeginMeleeSwing"/>: the old arc-less SubmitAttack melee path accepted a claimed
        /// hit point out to about 4 m with no arc and no walls. Fixtures that deliberately use that path as a
        /// damage-formula probe turn it on explicitly. Enemies (no swing pose) are not affected.
        /// </summary>
        internal bool AllowLegacyMeleeAttacks;

        /// <summary>
        /// When true a weapon with a combo chain also accepts any of its OTHER swing patterns at combo step 0 (the D1a
        /// rule: no chain multiplier, the pattern's own timing, no chain state). Default false: the chain's first step is
        /// the only step-0 pattern. Fixtures that probe the sweep geometry with a specific pattern (overhead, slash
        /// back) opt in.
        /// </summary>
        internal bool AllowLegacyPatternSelection;

        /// <summary>
        /// When false every contact does full damage whatever the impact-zone bands say. Default true. TEST SEAM for
        /// fixtures whose assertion is about something else (the exact location table, a flat damage formula) and
        /// whose targets sit at arbitrary distances along the blade; the zones themselves are pinned by the D1b gate.
        /// </summary>
        internal bool ImpactZonesEnabled = true;

        /// <summary>
        /// Gate seam: geometry fixtures raise a guard and strike in the same instant. They turn the ready delay off and say so;
        /// the delay itself is proven where it is switched back on. Always true in the game.
        /// </summary>
        internal bool GuardReadyDelayEnabled = true;

        /// <summary>Guard breaks the authority has applied (a defender's guard was engaged and its impact resist reached).</summary>
        public int GuardBreaksApplied { get; private set; }

        /// <summary>Hit-slow statuses the authority has put on a target.</summary>
        public int HitSlowsApplied { get; private set; }

        private CombatFlowTuning FlowTuning
        {
            get
            {
                if (config != null && config.flow != null) return config.flow;
                return _defaultFlow ?? (_defaultFlow = CombatFlowTuning.Default);
            }
        }

        /// <summary>Swings accepted by TryBeginMeleeSwing.</summary>
        public int SwingsStarted { get; private set; }

        /// <summary>Swings currently running (windup, active or recovery).</summary>
        public int ActiveSwingCount => _activeSwings.Count;

        private HitLocationTable LocationTable =>
            config != null && config.hitLocations != null ? config.hitLocations : HitLocationTable.Default;

        /// <summary>
        /// D1c5: what can stop a swing. The level mask MINUS every prop layer (interact and loot layers, plus the config's
        /// interact mask): chests, the Ember Gate pillars and loot are walk-through for a blade by design (a sword does not
        /// stop on a chest between the attacker and the target; walls are what a swing recoils from). Trigger colliders are
        /// ignored by every query below (QueryTriggerInteraction.Ignore). Derived, not serialized: a config whose level mask
        /// was widened to include a prop layer still cannot make props blockers.
        /// </summary>
        internal LayerMask SwingBlockerMask
        {
            get
            {
                int level = config != null ? config.levelMask.value : LayerMask.GetMask(GameConfig.LevelLayerName);
                int props = LayerMask.GetMask(GameConfig.InteractLayerName, GameConfig.LootLayerName);
                if (config != null) props |= config.interactMask.value;
                return level & ~props;
            }
        }

        private void FixedUpdate()
        {
            if (AutoStepSwings) StepSwings();
        }

        // ------------------------------------------------------------------------------------------------
        // Begin
        // ------------------------------------------------------------------------------------------------

        public bool TryBeginMeleeSwing(in MeleeSwingIntent intent, out int swingId, out string reason)
        {
            swingId = 0;
            reason = string.Empty;
            LastAttackRejectReason = string.Empty;
            if (!LegacyAllowed(intent.InstigatorCombatId, "swing", false, out reason)) return false;

            if (!CombatRegistry.TryGet(intent.InstigatorCombatId, out ICombatEntity instigator)) return RejectSwing(intent, "unknown instigator id", out reason);
            if (instigator.IsDead)
            {
                _chains.Remove(intent.InstigatorCombatId);
                return RejectSwing(intent, "instigator is dead", out reason);
            }

            // D1c5 (M-c, authority half): block and attack are exclusive. Nothing is stamped or spent on a refusal.
            if (instigator is IGuardStateSource guardState && guardState.IsGuarding)
            {
                return RejectSwing(intent, "instigator is guarding: lower the guard before swinging", out reason);
            }

            if (!(instigator is IActiveWeaponProvider holder)) return RejectSwing(intent, "instigator has no authoritative weapon", out reason);
            WeaponData weapon = holder.ActiveWeapon;
            if (weapon == null) return RejectSwing(intent, "instigator holds no weapon", out reason);
            if (intent.WeaponId != weapon.weaponId)
            {
                return RejectSwing(intent, $"claimed weapon '{intent.WeaponId}' is not the equipped weapon '{weapon.weaponId}'", out reason);
            }

            if (!weapon.UsesSweptMelee) return RejectSwing(intent, "the equipped weapon has no swing patterns", out reason);
            if (!(instigator is IAimPoseProvider actor)) return RejectSwing(intent, "instigator has no authoritative aim pose", out reason);

            // D1b: the step is validated against the weapon's chain AND the authority's own chain state for this
            // instigator. Nothing the client claims about "where it is in the chain" is believed: a step is accepted
            // only when it is step 0 (a fresh chain) or exactly the step after the one the authority last accepted,
            // within the combo window of that step's recovery, for the same weapon.
            SwingPatternData pattern;
            ComboStep stepData;
            string stepReason;
            bool resolved;
            // U3C3: a riposte strike is begun by TryBeginRiposte / TryContinueRiposte only (they set the override after checking the
            // server's parry record). Its pattern and damage multiplier come from the weapon data, never from the intent.
            bool riposte = _riposteOverride.Active;
            bool legacyPick = riposte || (AllowLegacyPatternSelection && weapon.HasComboChain && intent.ComboStep == 0
                              && intent.PatternIndex != weapon.comboSteps[0].patternIndex);
            if (riposte)
            {
                int riposteIndex = _riposteOverride.PatternIndex;
                pattern = weapon.swingPatterns != null && riposteIndex >= 0 && riposteIndex < weapon.swingPatterns.Length ? weapon.swingPatterns[riposteIndex] : null;
                resolved = pattern != null;
                stepData = new ComboStep(riposteIndex, _riposteOverride.Multiplier);
                stepReason = $"riposte pattern {riposteIndex} is not a pattern of the equipped weapon";
            }
            else if (legacyPick)
            {
                resolved = weapon.TryResolveSwingPattern(0, intent.PatternIndex, out pattern);
                stepData = new ComboStep(intent.PatternIndex, 1f);
                stepReason = $"pattern {intent.PatternIndex} at combo step 0 is not allowed for the equipped weapon";
            }
            else
            {
                resolved = weapon.TryResolveComboStep(intent.ComboStep, intent.PatternIndex, out stepData, out pattern, out stepReason);
            }

            if (!resolved || !pattern.Validate(out _)) return RejectSwing(intent, stepReason, out reason);

            if (weapon.HasComboChain && intent.ComboStep > 0 && !ChainAllows(intent.InstigatorCombatId, instigator, weapon, intent.ComboStep, out string chainReason))
            {
                return RejectSwing(intent, chainReason, out reason);
            }

            SwingTiming authoredTiming = legacyPick ? pattern.Timing : weapon.ComboTiming(intent.ComboStep, pattern);
            float actionFactor = instigator is IActionSpeedSource speedSource ? speedSource.ActionSpeedFactor : 1f;
            MeleeActionTiming action = ActionSpeedRules.Resolve(authoredTiming.Windup, authoredTiming.Active,
                authoredTiming.Recovery, authoredTiming.WallRecoil, weapon.cooldown, actionFactor, cooldownGrace);
            SwingTiming timing = new SwingTiming(action.Windup, action.Active, action.Recovery, action.WallRecoil);

            if (_lastSequence.TryGetValue(intent.InstigatorCombatId, out int lastSequence) && intent.Sequence <= lastSequence)
            {
                return RejectSwing(intent, "stale or repeated command sequence", out reason);
            }
            _lastSequence[intent.InstigatorCombatId] = intent.Sequence;

            // Bring a running swing up to this instant first: whether it is over must not depend on when the last fixed step
            // happened (a client whose cooldown equals the swing length, like the combo finisher, would otherwise be refused
            // for up to one tick after the swing really ended).
            if (_swingByInstigator.TryGetValue(intent.InstigatorCombatId, out SwingRecord running)) StepSwing(running, Now);
            if (_swingByInstigator.ContainsKey(intent.InstigatorCombatId)) return RejectSwing(intent, "a swing is already in progress", out reason);
            // Reference port: a parried attacker is out of action for its "deflected" time; a defender that has just blocked may answer
            // with its riposte only, not with an ordinary attack, until its block reaction ends.
            if (DeflectedRemaining(intent.InstigatorCombatId) > 0f) return RejectSwing(intent, "deflected by a parry", out reason);
            if (!riposte && BlockReactionRemaining(intent.InstigatorCombatId) > 0f) return RejectSwing(intent, "block reaction in progress", out reason);

            float now = Now;
            float cooldown = Mathf.Max(0.1f, weapon.cooldown);
            // The interval between two attacks is the weapon cooldown on the authority clock, and never less than the
            // full length (windup + active + recovery) of the swing that was last accepted: a forged client cannot
            // swing faster than the pattern, even when a wall stop ended the previous swing early.
            float minimumInterval = _lastAttackTime.TryGetValue(intent.InstigatorCombatId, out float previousAttack)
                ? FrozenRepeatInterval(intent.InstigatorCombatId, previousAttack, cooldown * cooldownGrace)
                : cooldown * cooldownGrace;

            if (!_riposteOverride.SkipCooldown && _lastAttackTime.TryGetValue(intent.InstigatorCombatId, out float lastAttack) && now - lastAttack < minimumInterval)
            {
                return RejectSwing(intent, "attack faster than weapon cooldown", out reason);
            }

            if (!actor.TryGetAimPose(out Vector3 eye, out float yaw, out float pitch) || !IsFinite(eye))
            {
                return RejectSwing(intent, "instigator has no usable aim pose", out reason);
            }

            Vector3 claimed = Quaternion.Euler(intent.AimPitch, intent.AimYaw, 0f) * Vector3.forward;
            Vector3 actual = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
            if (float.IsNaN(intent.AimYaw) || float.IsNaN(intent.AimPitch) || float.IsInfinity(intent.AimYaw) || float.IsInfinity(intent.AimPitch)
                || !(Vector3.Angle(claimed, actual) <= swingAimTolerance))
            {
                return RejectSwing(intent, "aim snapshot does not match the authoritative pose", out reason);
            }

            // Accepted: only an accepted begin spends the cooldown (same clock and stamp as weapon attacks).
            _lastAttackTime[intent.InstigatorCombatId] = now;
            _lastSwingFloor[intent.InstigatorCombatId] = new KeyValuePair<float, float>(now, action.RepeatInterval);

            // The chain moves on only for an accepted step: the next step is this one + 1 and its window opens when this
            // step's recovery begins. The last step resets the chain (the finisher's longer recovery is in the timing).
            if (weapon.HasComboChain && !legacyPick)
            {
                if (intent.ComboStep >= weapon.comboSteps.Length - 1)
                {
                    _chains.Remove(intent.InstigatorCombatId);
                }
                else
                {
                    _chains[intent.InstigatorCombatId] = new ChainState
                    {
                        Weapon = weapon,
                        Instigator = instigator,
                        WeaponEpoch = WeaponEpochOf(instigator),
                        NextStep = intent.ComboStep + 1,
                        WindowEnd = now + timing.ActiveEnd + weapon.ComboWindowAfter(intent.ComboStep)
                    };
                }
            }
            else
            {
                _chains.Remove(intent.InstigatorCombatId);
            }

            SwingRecord record = new SwingRecord
            {
                Id = _nextSwingId++,
                InstigatorId = intent.InstigatorCombatId,
                Instigator = instigator,
                Actor = actor,
                Weapon = weapon,
                WeaponEpoch = WeaponEpochOf(instigator),
                Pattern = pattern,
                Timing = timing,
                RepeatInterval = action.RepeatInterval,
                ActionSpeedFactor = action.Factor,
                // Preserve the authored normalized blade-path contact window, including the active floor.
                ContactWindow = timing.Active * Mathf.Clamp01(pattern.contactWindow / authoredTiming.Active),
                ComboStep = intent.ComboStep,
                StepMultiplier = Mathf.Max(0.05f, stepData.damageMultiplier),
                Sequence = intent.Sequence,
                RiposteStrike = riposte ? _riposteOverride.Strike : 0,
                StartTime = now,
                SampleCount = SwingGeometry.SampleCount(pattern, weapon, FlowTuning.GridSwingTurnCeiling, timing.Active),
                TurnCeiling = FlowTuning.ResolvedSwingTurnCeiling,
                BladeLength = pattern.BladeLength(weapon),
                LastTs = 0f,
                LastEye = eye,
                LastYaw = yaw,
                LastPitch = pitch,
                CommitAim = FlowTuning.commitSwingAim,
                CommitYaw = yaw,
                CommitPitch = pitch,
                Phase = timing.Windup > 0f ? SwingPhase.Windup : SwingPhase.Active
            };
            record.Warning = BladeWarning.Describe(pattern, weapon, eye, yaw, pitch, FlowTuning, record.SampleCount);

            _swingsById[record.Id] = record;
            _swingByInstigator[record.InstigatorId] = record;
            _activeSwings.Add(record);
            SwingsStarted++;

            swingId = record.Id;
            return true;
        }

        /// <summary>The last accepted melee interval governs until a different attack stamps the shared clock.</summary>
        private float FrozenRepeatInterval(int combatId, float lastAttack, float fallback)
        {
            return _lastSwingFloor.TryGetValue(combatId, out KeyValuePair<float, float> prior) && prior.Key == lastAttack
                ? prior.Value : fallback;
        }

        public bool TryGetMeleeSwingStatus(int swingId, out MeleeSwingStatus status)
        {
            if (!_swingsById.TryGetValue(swingId, out SwingRecord r))
            {
                status = default;
                return false;
            }

            float elapsed = r.Phase == SwingPhase.Finished ? r.EndTs : Now - r.StartTime;
            if (r.HitsView == null) r.HitsView = r.Hits.AsReadOnly();
            status = new MeleeSwingStatus(r.Id, r.InstigatorId, r.Phase, r.EndReason, elapsed,
                Mathf.Min(r.NextSample, r.SampleCount + 1), r.SampleCount, r.WallSample, r.HitsView,
                r.ComboStep, r.StepMultiplier, r.Timing, r.TurnClamped, r.WallPoint, r.WallNormal, r.HasWallPoint, r.RepeatInterval, r.ActionSpeedFactor,
                r.Warning, r.CommitAim, r.RiposteStrike, new SwingFrame(r.LastEye, r.LastYaw, r.LastPitch));
            return true;
        }

        /// <summary>
        /// B2: read-only combo state for the HUD (see <see cref="IComboStateSource"/>). Uses the same liveness rule as
        /// <see cref="TryGetExpectedComboStep"/>; never mutates the chain.
        /// </summary>
        public bool TryGetComboState(int instigatorCombatId, out int step, out int total, out float windowRemaining)
        {
            step = 0;
            total = 0;
            windowRemaining = 0f;
            if (!CombatRegistry.TryGet(instigatorCombatId, out ICombatEntity instigator)) return false;
            if (!(instigator is IActiveWeaponProvider holder) || holder.ActiveWeapon == null || !holder.ActiveWeapon.HasComboChain) return false;

            WeaponData weapon = holder.ActiveWeapon;
            total = weapon.ComboLength;
            if (_chains.TryGetValue(instigatorCombatId, out ChainState chain) && ChainIsLive(chain, instigator) && chain.Weapon == weapon)
            {
                step = Mathf.Clamp(chain.NextStep, 0, total);
                windowRemaining = Mathf.Max(0f, chain.WindowEnd - Now);
            }
            else if (_swingByInstigator.TryGetValue(instigatorCombatId, out SwingRecord running) && running.Phase != SwingPhase.Finished
                     && running.Weapon == weapon && running.ComboStep >= total - 1)
            {
                step = total;   // the finisher is being swung: the chain is complete until it ends
            }

            return true;
        }

        /// <summary>
        /// The combo step this authority would accept next from the instigator (0 = start a chain). The chain state is
        /// the authority's: it forgets the chain when the combo window ran out, the weapon in hand changed, the
        /// instigator died or went away, a wall stopped the last swing, or the last step was swung.
        /// </summary>
        public bool TryGetExpectedComboStep(int instigatorCombatId, out int step)
        {
            step = 0;
            if (!CombatRegistry.TryGet(instigatorCombatId, out ICombatEntity instigator)) return false;
            if (_chains.TryGetValue(instigatorCombatId, out ChainState chain) && ChainIsLive(chain, instigator)) step = chain.NextStep;
            return true;
        }

        private bool ChainIsLive(ChainState chain, ICombatEntity instigator)
        {
            if (chain == null || chain.Instigator != instigator || instigator.IsDead) return false;
            if (!(instigator is IActiveWeaponProvider holder) || holder.ActiveWeapon != chain.Weapon) return false;
            if (WeaponEpochOf(instigator) != chain.WeaponEpoch) return false;
            return Now <= chain.WindowEnd;
        }

        /// <summary>The instigator's weapon-change counter (0 when it does not report one).</summary>
        private static int WeaponEpochOf(ICombatEntity entity) => entity is IWeaponChangeSource source ? source.WeaponChangeCount : 0;

        private bool ChainAllows(int instigatorId, ICombatEntity instigator, WeaponData weapon, int step, out string reason)
        {
            reason = string.Empty;
            if (!_chains.TryGetValue(instigatorId, out ChainState chain) || chain.Weapon != weapon || chain.Instigator != instigator)
            {
                reason = $"no running combo chain for the equipped weapon: step {step} needs step {step - 1} first";
                return false;
            }

            if (WeaponEpochOf(instigator) != chain.WeaponEpoch)
            {
                reason = $"the weapon changed since combo step {step - 1}: step {step} needs a fresh chain";
                return false;
            }

            if (step != chain.NextStep)
            {
                reason = $"combo step {step} is not the expected next step {chain.NextStep}";
                return false;
            }

            if (Now > chain.WindowEnd)
            {
                reason = $"combo window expired ({Now - chain.WindowEnd:F2} s late)";
                return false;
            }

            return true;
        }

        // ------------------------------------------------------------------------------------------------
        // Combat flow state read by movement and the guard (ICombatFlowState)
        // ------------------------------------------------------------------------------------------------

        public float GetMoveFactor(int combatId)
        {
            float factor = 1f;
            float now = Now;

            if (_swingByInstigator.TryGetValue(combatId, out SwingRecord rec) && rec.Phase != SwingPhase.Finished)
            {
                float elapsed = now - rec.StartTime;
                if (rec.Stopped)
                {
                    // the recoil after a wall stop is a recovery; once the recoil time has passed the swing is over
                    if (elapsed < rec.StopTs + rec.Timing.WallRecoil) factor *= rec.Pattern.recoveryMoveMultiplier;
                }
                else
                {
                    factor *= CombatFlowRules.SwingPhaseFactor(rec.Pattern, rec.Timing, elapsed, false);
                }
            }

            if (IsGuardStaggered(combatId)) factor *= Mathf.Clamp(FlowTuning.guardStaggerMoveMultiplier, 0.1f, 1f);
            if (BlockReactionRemaining(combatId) > 0f) factor *= Mathf.Clamp(FlowTuning.blockReactionMoveMultiplier, 0.1f, 1f);
            if (DeflectedRemaining(combatId) > 0f) factor *= Mathf.Clamp(FlowTuning.deflectedMoveMultiplier, 0.1f, 1f);
            return factor;
        }

        /// <summary>
        /// True while the instigator's swing is running at the authority time <see cref="Now"/>: it is evaluated against
        /// the clock, not against the last fixed step, so it flips at exactly the swing's end time (windup + active +
        /// recovery, or the wall stop's recoil end) even when no step ran since. Death or a weapon change are still
        /// noticed by the next step (they are events, not time).
        /// </summary>
        public bool IsSwingInProgress(int combatId)
        {
            if (!_swingByInstigator.TryGetValue(combatId, out SwingRecord r) || r.Phase == SwingPhase.Finished) return false;

            float end = r.Stopped ? r.StopTs + r.Timing.WallRecoil : r.Timing.Total;
            return Now - r.StartTime < end;
        }

        public bool IsGuardStaggered(int combatId) => GuardStaggerRemaining(combatId) > 0f;

        public float GuardStaggerRemaining(int combatId) =>
            _guardBrokenUntil.TryGetValue(combatId, out float until) ? Mathf.Max(0f, until - Now) : 0f;

        /// <summary>Seconds a raised guard needs before it intercepts (tuning; 0 = immediately).</summary>
        public float GuardReadySeconds => GuardReadyDelayEnabled ? Mathf.Max(0f, FlowTuning.guardReadySeconds) : 0f;

        /// <summary>Seconds left of the entity's block reaction: it blocked a hit and cannot begin an ordinary attack yet (0 when none).</summary>
        public float BlockReactionRemaining(int combatId) =>
            _blockReactionUntil.TryGetValue(combatId, out float until) ? Mathf.Max(0f, until - Now) : 0f;

        /// <summary>Seconds left of the entity's deflected state: its hit was parried and it cannot begin an attack yet (0 when none).</summary>
        public float DeflectedRemaining(int combatId) =>
            _deflectedUntil.TryGetValue(combatId, out float until) ? Mathf.Max(0f, until - Now) : 0f;

        /// <summary>Block reactions and deflected states applied (observability for gates and tests).</summary>
        public int BlockReactionsApplied { get; private set; }
        public int DeflectsApplied { get; private set; }

        /// <summary>The swing currently running for an instigator, or 0.</summary>
        public int ActiveSwingIdFor(int instigatorCombatId) =>
            _swingByInstigator.TryGetValue(instigatorCombatId, out SwingRecord r) ? r.Id : 0;

        private bool RejectSwing(in MeleeSwingIntent intent, string reason, out string reasonOut)
        {
            reasonOut = reason;
            LastAttackRejectReason = reason;
            RejectedCount++;
            if (logRejections)
            {
                CombatDiagnostics.Log($"reject-swing:{intent.InstigatorCombatId}:{reason}",
                    $"[CombatAuthority] Rejected melee swing from #{intent.InstigatorCombatId} ({intent.WeaponId}): {reason}", this);
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------
        // Step
        // ------------------------------------------------------------------------------------------------

        /// <summary>Advances every running swing to the authority clock. Idempotent for an unchanged clock.</summary>
        internal void StepSwings()
        {
            ForgetDeadChains();
            PruneEntityState();
            if (_activeSwings.Count == 0) return;

            float now = Now;
            _stepBuffer.Clear();
            _stepBuffer.AddRange(_activeSwings);
            for (int i = 0; i < _stepBuffer.Count; i++) StepSwing(_stepBuffer[i], now);
        }

        /// <summary>Drops the combo chain of an instigator that died, vanished or swapped its weapon (checked on every step).</summary>
        private void ForgetDeadChains()
        {
            if (_chains.Count == 0) return;

            _chainBuffer.Clear();
            foreach (KeyValuePair<int, ChainState> pair in _chains)
            {
                ChainState chain = pair.Value;
                bool live = CombatRegistry.TryGet(pair.Key, out ICombatEntity current) && current == chain.Instigator && !current.IsDead
                            && current is IActiveWeaponProvider holder && holder.ActiveWeapon == chain.Weapon
                            && WeaponEpochOf(current) == chain.WeaponEpoch;
                if (!live) _chainBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _chainBuffer.Count; i++) _chains.Remove(_chainBuffer[i]);
        }

        private readonly List<int> _pruneBuffer = new List<int>(4);

        /// <summary>Number of entries in the per-entity dictionaries (observability for gates).</summary>
        internal int GuardBrokenEntryCount => _guardBrokenUntil.Count;
        internal int SwingFloorEntryCount => _lastSwingFloor.Count;
        internal int ChainEntryCount => _chains.Count;

        /// <summary>
        /// (The swing-side dictionaries only. The base anti-replay state, _lastAttackTime and _lastSequence, is deliberately kept:
        /// it is the cooldown / replay memory and an entity that is disabled and enabled again must not get it reset.)
        /// Drops per-entity bookkeeping of entities that are no longer registered (removed, unregistered, scene torn
        /// down) and expired or dead-target guard-break stamps. Live entities are untouched: an entry is only removed when
        /// the registry no longer knows the id, or (guard break) when the stagger is over or the entity died.
        /// </summary>
        private void PruneEntityState()
        {
            PruneUnregistered(_lastSwingFloor);
            PruneUnregistered(_chains);
            _riposte.Prune(RegisteredId);

            PruneExpired(_guardBrokenUntil);
            PruneExpired(_blockReactionUntil);
            PruneExpired(_deflectedUntil);
        }

        private void PruneExpired(Dictionary<int, float> untilById)
        {
            if (untilById.Count == 0) return;
            float now = Now;
            _pruneBuffer.Clear();
            foreach (KeyValuePair<int, float> pair in untilById)
            {
                if (pair.Value <= now || !CombatRegistry.TryGet(pair.Key, out ICombatEntity e) || e.IsDead) _pruneBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _pruneBuffer.Count; i++) untilById.Remove(_pruneBuffer[i]);
        }

        private void PruneUnregistered<T>(Dictionary<int, T> map)
        {
            if (map.Count == 0) return;
            _pruneBuffer.Clear();
            foreach (KeyValuePair<int, T> pair in map)
            {
                if (!CombatRegistry.TryGet(pair.Key, out _)) _pruneBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _pruneBuffer.Count; i++) map.Remove(_pruneBuffer[i]);
        }

        private void StepSwing(SwingRecord rec, float now)
        {
            if (rec.Phase == SwingPhase.Finished) return;

            float ts = now - rec.StartTime;

            if (!CombatRegistry.TryGet(rec.InstigatorId, out ICombatEntity current) || current != rec.Instigator)
            {
                FinishSwing(rec, SwingEndReason.InstigatorGone, Mathf.Max(0f, ts));
                return;
            }

            if (rec.Instigator.IsDead)
            {
                rec.Pending.Clear();
                FinishSwing(rec, SwingEndReason.InstigatorDied, Mathf.Max(0f, ts));
                return;
            }

            // A weapon swap ends the swing: what is in the hand now is not the weapon this segment belongs to.
            if (!(rec.Instigator is IActiveWeaponProvider holder) || holder.ActiveWeapon != rec.Weapon
                || WeaponEpochOf(rec.Instigator) != rec.WeaponEpoch)
            {
                rec.Pending.Clear();
                FinishSwing(rec, SwingEndReason.WeaponChanged, Mathf.Max(0f, ts));
                return;
            }

            if (!rec.Actor.TryGetAimPose(out Vector3 eye, out float yaw, out float pitch) || !IsFinite(eye))
            {
                eye = rec.LastEye;
                yaw = rec.LastYaw;
                pitch = rec.LastPitch;
            }

            if (float.IsNaN(yaw) || float.IsInfinity(yaw) || float.IsNaN(pitch) || float.IsInfinity(pitch))
            {
                yaw = rec.LastYaw;
                pitch = rec.LastPitch;
            }

            // U3C: a committed swing keeps the aim it began with (eye still follows the body): the warning stays exact, nothing tracks.
            if (rec.CommitAim)
            {
                yaw = rec.CommitYaw;
                pitch = rec.CommitPitch;
            }

            // D1c2: the turn ceiling. The pose the sweep uses is a rate-limited FOLLOWER of the reported pose: from the last
            // step it may advance at most ceiling x dt degrees in yaw (shortest way round) and in pitch, in every phase of
            // the swing. What is held back is discarded (never banked), so a flick cannot be "paid back" later; a turn
            // inside the budget is passed through untouched, so honest play is bit-identical to the unclamped sweep.
            if (ts > rec.LastTs && !float.IsInfinity(rec.TurnCeiling))
            {
                float budget = rec.TurnCeiling * (ts - rec.LastTs);
                float dYaw = Mathf.DeltaAngle(rec.LastYaw, yaw);
                if (Mathf.Abs(dYaw) > budget)
                {
                    rec.TurnClamped += Mathf.Abs(dYaw) - budget;
                    yaw = rec.LastYaw + Mathf.Sign(dYaw) * budget;
                }

                float dPitch = pitch - rec.LastPitch;
                if (Mathf.Abs(dPitch) > budget)
                {
                    rec.TurnClamped += Mathf.Abs(dPitch) - budget;
                    pitch = rec.LastPitch + Mathf.Sign(dPitch) * budget;
                }
            }

            SwingPatternData pattern = rec.Pattern;
            float windup = rec.Timing.Windup;
            float active = rec.Timing.Active;

            if (ts > rec.LastTs)
            {
                while (!rec.Stopped && rec.NextSample <= rec.SampleCount)
                {
                    float sampleTime = windup + active * rec.NextSample / rec.SampleCount;
                    if (sampleTime > ts) break;

                    float span = ts - rec.LastTs;
                    float k = span <= 1e-6f ? 1f : Mathf.Clamp01((sampleTime - rec.LastTs) / span);
                    SwingFrame frame = new SwingFrame(Vector3.Lerp(rec.LastEye, eye, k),
                        Mathf.LerpAngle(rec.LastYaw, yaw, k), Mathf.Lerp(rec.LastPitch, pitch, k));

                    ProcessSample(rec, rec.NextSample, sampleTime, frame);
                    rec.NextSample++;
                }

                rec.LastTs = ts;
                rec.LastEye = eye;
                rec.LastYaw = yaw;
                rec.LastPitch = pitch;
            }

            if (rec.Stopped)
            {
                rec.Phase = SwingPhase.Recovery;
                if (ts >= rec.StopTs + rec.Timing.WallRecoil) FinishSwing(rec, rec.EndReason, rec.StopTs + rec.Timing.WallRecoil);
                return;
            }

            if (rec.NextSample > rec.SampleCount)
            {
                ResolveAllPending(rec, windup + active);
                rec.Phase = SwingPhase.Recovery;
                if (ts >= rec.Timing.Total) FinishSwing(rec, SwingEndReason.Completed, rec.Timing.Total);
                return;
            }

            rec.Phase = ts < windup ? SwingPhase.Windup : SwingPhase.Active;
        }

        private void ProcessSample(SwingRecord rec, int sample, float sampleTime, in SwingFrame frame)
        {
            SwingPatternData pattern = rec.Pattern;
            SwingGeometry.PathSegment(frame, pattern, rec.BladeLength, sample / (float)rec.SampleCount, out Vector3 bladeBase, out Vector3 bladeTip);

            Vector3 axis = bladeTip - bladeBase;
            float length = axis.magnitude;
            Vector3 dir = length > 1e-5f ? axis / length : Vector3.forward;

            // Level geometry first. A wall ALWAYS clips the segment where it meets it (nothing is struck through
            // a wall); the swing STOPS (recoil) when the blade is moving into the surface it touched. A blade that
            // merely touches a wall while moving away from it or along it (the start of a slash that begins
            // pointing at a side wall, in a corridor) is clipped but keeps going. A base buried in a wall clips
            // the segment to nothing and always stops the swing.
            float clip = length;
            bool wall = false;
            Vector3 wallPoint = default;
            Vector3 wallNormal = default;
            Collider breakableContact = null;
            LayerMask mask = SwingBlockerMask;
            if (mask.value != 0)
            {
                // The line from the eye to the hand is tested too: a swing cannot start around an outside corner
                // (hand on the far side of a wall edge the eye cannot see past). Treated like a buried base.
                if (Physics.Linecast(frame.Eye, bladeBase, out RaycastHit handHit, mask, QueryTriggerInteraction.Ignore))
                {
                    clip = 0f;
                    wall = true;
                    wallPoint = handHit.point;
                    wallNormal = handHit.normal;
                }
                else if (Physics.CheckSphere(bladeBase, pattern.sweepRadius * 0.5f, mask, QueryTriggerInteraction.Ignore))
                {
                    clip = 0f;
                    wall = true;
                    wallPoint = bladeBase;
                }
                else if (Physics.Linecast(bladeBase, bladeTip, out RaycastHit wallHit, mask, QueryTriggerInteraction.Ignore))
                {
                    clip = Mathf.Min(length, wallHit.distance);
                    wall = MovesIntoSurface(rec, sample, frame, clip, wallHit.normal);
                    wallPoint = wallHit.point;
                    wallNormal = wallHit.normal;
                    breakableContact = wallHit.collider;
                }
            }

            Vector3 clippedTip = bladeBase + dir * clip;
            float reach = pattern.EffectiveReach(rec.Weapon) + swingBroadPhaseMargin;
            float reachSqr = reach * reach;
            float sweepRadius = pattern.sweepRadius;
            float sampleDt = rec.Timing.Active / rec.SampleCount;
            int windowSamples = Mathf.Clamp(Mathf.CeilToInt(rec.ContactWindow / sampleDt), 0, rec.SampleCount);

            _registryBuffer.Clear();
            IReadOnlyList<ICombatEntity> all = CombatRegistry.All;
            for (int i = 0; i < all.Count; i++) _registryBuffer.Add(all[i]);

            // Gather body and blade contacts before selecting targets, independently of registry order.
            // A held interception ends later contacts/samples; a broken guard permits continuation.
            _sampleContacts.Clear();
            for (int i = 0; i < _registryBuffer.Count; i++)
            {
                ICombatEntity target = _registryBuffer[i];
                if (!(target is PlayerRig defender) || target == rec.Instigator || target.IsDead
                    || !defender.gameObject.activeInHierarchy || !CombatTeams.AreHostile(rec.Instigator, target)
                    || rec.Hit.Contains(target.CombatId) || FindPending(rec, target.CombatId) != null
                    || (target.CombatCenter - frame.Eye).sqrMagnitude > reachSqr
                    || !defender.TrySweepGuard(bladeBase, clippedTip, sweepRadius, out float distance, out bool shieldContact, out bool weaponContact)) continue;
                _shapeBuffer.Clear();
                defender.CollectHitShapes(_shapeBuffer);
                float bodyDistance = float.PositiveInfinity;
                for (int j = 0; j < _shapeBuffer.Count; j++)
                {
                    HitShape shape = _shapeBuffer[j];
                    if (TrySweepCapsule(bladeBase, dir, clip, shape.A, shape.B,
                            shape.Radius + sweepRadius, out float entry)) bodyDistance = Mathf.Min(bodyDistance, entry);
                }
                if (distance > bodyDistance) continue;
                _sampleContacts.Add(new Contact
                {
                    Target = target, FirstSample = sample, LastWindowSample = sample,
                    Location = HitLocation.Unspecified, Point = bladeBase + dir * distance,
                    T = length > 1e-5f ? distance / length : 0f, GuardBladeContact = !shieldContact && !weaponContact, GuardShieldContact = shieldContact, GuardWeaponContact = weaponContact, EntryDistance = distance
                });
            }
            for (int i = 0; i < _registryBuffer.Count; i++)
            {
                ICombatEntity target = _registryBuffer[i];
                if (target == null || target == rec.Instigator || target.IsDead) continue;
                if (rec.Hit.Contains(target.CombatId)) continue;

                // A deactivated object is not in the world (the old overlap query ignored its colliders too). A
                // merely DISABLED agent (a frozen test enemy, a stunned monster) is still there and can be hit.
                if (target is Component component && !component.gameObject.activeInHierarchy) continue;
                if (!CombatTeams.AreHostile(rec.Instigator, target)) continue;
                if (!(target is IHitShapeProvider provider)) continue;
                if ((target.CombatCenter - frame.Eye).sqrMagnitude > reachSqr) continue;

                _shapeBuffer.Clear();
                provider.CollectHitShapes(_shapeBuffer);

                float entryDistance = float.PositiveInfinity;
                float bestDepth = -1f;
                HitLocation bestLocation = HitLocation.Unspecified;
                Vector3 bestPoint = default;
                float bestT = 0f;
                for (int s = 0; s < _shapeBuffer.Count; s++)
                {
                    HitShape shape = _shapeBuffer[s];
                    if (TrySweepCapsule(bladeBase, dir, clip, shape.A, shape.B,
                            shape.Radius + sweepRadius, out float entry)) entryDistance = Mathf.Min(entryDistance, entry);
                    float depth = SwingGeometry.SweepDepth(bladeBase, clippedTip, shape.A, shape.B, shape.Radius, sweepRadius, out Vector3 onBlade);
                    if (depth < 0f) continue;

                    if (IsBetterContact(depth, shape.Location, bestDepth, bestLocation))
                    {
                        bestDepth = depth;
                        bestLocation = shape.Location;
                        bestPoint = onBlade;

                        // D1b: where on the damage segment the contact is. The closest point of the BLADE to the
                        // volume, measured from the hilt end along the full (unclipped) segment: the authority's
                        // own geometry, nothing the client claims.
                        bestT = length > 1e-5f ? Mathf.Clamp01(Vector3.Distance(bladeBase, onBlade) / length) : 0f;
                    }
                }

                if (bestDepth < 0f) continue;

                // A hand on the grip can be the first flesh touched even when the blade
                // continues deeper into the head/torso in this same sample. Preserve that
                // Arms wound rather than upgrading it through the ordinary depth selector.
                bool gripContact = target is PlayerRig gripOwner && gripOwner.TryGetGuardGripHitShape(out HitShape grip)
                    && TrySweepCapsule(bladeBase, dir, clip, grip.A, grip.B, grip.Radius + sweepRadius, out float gripEntry)
                    && gripEntry <= entryDistance + .00001f;
                if (gripContact)
                {
                    bestLocation = HitLocation.Arms;
                    bestPoint = bladeBase + dir * entryDistance;
                    bestT = length > 1e-5f ? Mathf.Clamp01(entryDistance / length) : 0f;
                }

                _sampleContacts.Add(new Contact
                {
                    Target = target, FirstSample = sample, LastWindowSample = sample + windowSamples,
                    Location = bestLocation, Depth = bestDepth, Point = bestPoint, T = bestT,
                    GripContact = gripContact, EntryDistance = entryDistance
                });
            }

            // Flesh and metal compete for the same target budget in actual entry order.
            // Never reserve a slot for a farther guard before a nearer body was considered.
            _sampleContacts.Sort((a, b) =>
            {
                int distance = a.EntryDistance.CompareTo(b.EntryDistance);
                if (distance != 0) return distance;
                int guard = (b.GuardBladeContact || b.GuardShieldContact || b.GuardWeaponContact).CompareTo(a.GuardBladeContact || a.GuardShieldContact || a.GuardWeaponContact);
                return guard != 0 ? guard : a.Target.CombatId.CompareTo(b.Target.CombatId);
            });
            bool guardStopped = false;
            for (int i = 0; i < _sampleContacts.Count; i++)
            {
                Contact candidate = _sampleContacts[i];
                if (rec.Hit.Contains(candidate.Target.CombatId)) continue;
                Contact pending = FindPending(rec, candidate.Target.CombatId);
                if (candidate.GuardBladeContact || candidate.GuardShieldContact || candidate.GuardWeaponContact)
                {
                    if (pending != null || rec.Hits.Count + rec.Pending.Count >= pattern.maxTargets) continue;
                    if (!ApplySwingHit(rec, candidate, sample, frame.Eye)) continue;
                    if (rec.Hits[rec.Hits.Count - 1].GuardBroken) continue;
                    guardStopped = true;
                    break;
                }
                if (pending == null)
                {
                    if (rec.Hits.Count + rec.Pending.Count < pattern.maxTargets) rec.Pending.Add(candidate);
                }
                else if (!pending.GripContact && !candidate.GripContact && sample <= pending.LastWindowSample
                    && IsBetterContact(candidate.Depth, candidate.Location, pending.Depth, pending.Location))
                {
                    pending.Depth = candidate.Depth;
                    pending.Location = candidate.Location;
                    pending.Point = candidate.Point;
                    pending.T = candidate.T;
                }
            }

            // Contacts whose window has closed become hits now, in the order they were first touched.
            for (int i = 0; i < rec.Pending.Count;)
            {
                Contact c = rec.Pending[i];
                if (c.LastWindowSample <= sample)
                {
                    rec.Pending.RemoveAt(i);
                    ApplySwingHit(rec, c, sample, frame.Eye);
                }
                else
                {
                    i++;
                }
            }

            if (guardStopped)
            {
                // Earlier wounds stay real; clipping prevented any new contact behind this guard.
                ResolveAllPending(rec, sampleTime, sample, frame.Eye);
                rec.Stopped = true;
                rec.StopTs = sampleTime;
                rec.WallSample = sample; // shared stop-sample clock; HasWallPoint remains false
                rec.EndReason = SwingEndReason.GuardStop;
                _chains.Remove(rec.InstigatorId);
            }
            else if (wall)
            {
                ResolveAllPending(rec, sampleTime, sample, frame.Eye);
                if (breakableContact != null)
                {
                    var barrel = breakableContact.GetComponentInParent<Emberdelve.Interaction.BreakableBarrel>();
                    if (barrel != null) barrel.ApplyWeaponDamage(rec.Weapon.baseDamage * rec.StepMultiplier);
                }
                rec.Stopped = true;
                rec.StopTs = sampleTime;
                // A wall stop only cuts the swing short with a recoil: the combo survives, and its window restarts when the recoil ends.
                if (_chains.TryGetValue(rec.InstigatorId, out ChainState kept) && kept.Weapon == rec.Weapon)
                {
                    kept.WindowEnd = rec.StartTime + sampleTime + rec.Timing.WallRecoil + rec.Weapon.ComboWindowAfter(rec.ComboStep);
                }
                rec.WallSample = sample;
                rec.WallPoint = wallPoint;
                rec.WallNormal = wallNormal;
                rec.HasWallPoint = true;
                rec.EndReason = SwingEndReason.WallStop;
            }
        }

        /// <summary>
        /// True when the blade point <paramref name="distance"/> along the segment of <paramref name="sample"/> is
        /// moving against the surface normal (into the surface). The velocity is the pattern's own motion between
        /// this sample and its neighbour in the same aim frame, so it does not depend on how the authority is stepped.
        /// </summary>
        private static bool MovesIntoSurface(SwingRecord rec, int sample, in SwingFrame frame, float distance, Vector3 surfaceNormal)
        {
            if (surfaceNormal.sqrMagnitude < 1e-6f) return true;

            int other = sample < rec.SampleCount ? sample + 1 : sample - 1;
            SwingGeometry.BladeSegment(frame, rec.Pattern.ActivePose(sample / (float)rec.SampleCount), rec.BladeLength, out Vector3 b0, out Vector3 t0);
            SwingGeometry.BladeSegment(frame, rec.Pattern.ActivePose(other / (float)rec.SampleCount), rec.BladeLength, out Vector3 b1, out Vector3 t1);

            Vector3 p0 = b0 + (t0 - b0).normalized * distance;
            Vector3 p1 = b1 + (t1 - b1).normalized * distance;
            Vector3 velocity = other > sample ? p1 - p0 : p0 - p1;
            return Vector3.Dot(velocity, surfaceNormal) < -1e-3f;
        }

        private static bool IsBetterContact(float depth, HitLocation location, float bestDepth, HitLocation bestLocation)
        {
            if (bestDepth < 0f) return true;
            if (depth > bestDepth + 1e-4f) return true;
            return Mathf.Abs(depth - bestDepth) <= 1e-4f && (int)location < (int)bestLocation;
        }

        private static Contact FindPending(SwingRecord rec, int targetId)
        {
            for (int i = 0; i < rec.Pending.Count; i++)
            {
                if (rec.Pending[i].Target.CombatId == targetId) return rec.Pending[i];
            }
            return null;
        }

        private void ResolveAllPending(SwingRecord rec, float time) => ResolveAllPending(rec, time, Mathf.Min(rec.NextSample, rec.SampleCount), rec.LastEye);

        private void ResolveAllPending(SwingRecord rec, float time, int sample, Vector3 eye)
        {
            while (rec.Pending.Count > 0)
            {
                Contact c = rec.Pending[0];
                rec.Pending.RemoveAt(0);
                ApplySwingHit(rec, c, sample, eye);
            }
        }

        private bool ApplySwingHit(SwingRecord rec, Contact contact, int sample, Vector3 eye)
        {
            ICombatEntity instigator = rec.Instigator;
            ICombatEntity target = contact.Target;
            if (instigator == null || instigator.IsDead || target == null || target.IsDead) return false;
            if (!CombatRegistry.TryGet(target.CombatId, out ICombatEntity registered) || registered != target) return false;
            if (rec.Hit.Contains(target.CombatId)) return false;

            float multiplier = LocationTable.Multiplier(contact.Location);
            Vector3 direction = contact.Point - eye;
            if (direction.sqrMagnitude < 0.0001f) direction = instigator.CombatCenter - target.CombatCenter;

            AttackIntent resolved = new AttackIntent(instigator.CombatId, instigator.Kind, rec.Weapon, 1f,
                instigator.CombatCenter, direction, rec.Pattern.EffectiveReach(rec.Weapon), rec.Sequence, Now);
            HitCandidate resolvedHit = new HitCandidate(target.CombatId, contact.Point, contact.Location == HitLocation.Head);

            // D1b impact zone: the weapon's bands applied to the contact parameter the sweep measured.
            float zone = ImpactZonesEnabled ? rec.Weapon.ImpactMultiplier(contact.T) : 1f;

            // FIX1: read the provider-side factors ONCE; the resolver and the hit report use the same values.
            float outgoing = OutgoingMultiplier(instigator, rec.Weapon);
            float flatBonus = FlatWeaponBonus(instigator, rec.Weapon);
            float armorRating = target is IArmorRatingProvider armorProvider ? DamageResolver.SanitizeArmorRating(armorProvider.GetArmorRating()) : 0f;
            DamageInfo damage = DamageResolver.ResolveSwing(rec.Weapon, rec.Pattern, resolved, resolvedHit,
                contact.Location, multiplier, target, outgoing,
                flatBonus, rec.StepMultiplier, zone, FlowTuning, contact.GuardBladeContact, contact.GuardShieldContact, contact.GuardWeaponContact);
            if (!damage.IsValid) return false;
            // An exhausted/nonfunctional guard cannot create damage just because metal was touched.
            // Continue the ordinary flesh sweep when the defense refused the interception.
            if ((contact.GuardBladeContact || contact.GuardShieldContact || contact.GuardWeaponContact) && !damage.Blocked) return false;

            rec.Hit.Add(target.CombatId);
            target.ApplyDamage(damage);
            bool parried = NoteMeleeResult(rec.InstigatorId, target, damage);
            if (parried)
            {
                float deflected = rec.Weapon != null && rec.Weapon.twoHanded ? FlowTuning.deflectedTwoHandedSeconds : FlowTuning.deflectedSeconds;
                if (deflected > 0f)
                {
                    _deflectedUntil[rec.InstigatorId] = Now + deflected;
                    DeflectsApplied++;
                }
            }
            AcceptedCount++;
            rec.Hits.Add(new MeleeSwingHit(target.CombatId, contact.Location, damage.Amount, damage.Blocked, sample, contact.FirstSample,
                contact.Point, contact.T, zone, damage.GuardBroken));
            ApplyHitEffects(target, damage, rec.Weapon);

            HitFeedback feedback = new HitFeedback(contact.Point, damage.Amount, damage.Blocked, damage.Headshot,
                target.IsDead, target.Kind, damage.Location);

            PublishCombatFeedback(instigator, target, feedback, damage, parried);
            ReportHit(CombatHitSource.Melee, instigator, target, rec.Weapon, damage, contact.Location, multiplier, rec.StepMultiplier, zone,
                contact.T, 1f, 0f, -1f, outgoing, flatBonus, armorRating, parried);
            return true;
        }

        private void FinishSwing(SwingRecord rec, SwingEndReason reason, float endTs)
        {
            if (rec.Phase == SwingPhase.Finished) return;

            rec.Phase = SwingPhase.Finished;
            rec.EndReason = reason;
            rec.EndTs = endTs;
            rec.Pending.Clear();

            // A swing that did not complete (the wielder died or left, the weapon was swapped) ends the combo. A wall stop does not.
            if (reason != SwingEndReason.Completed && reason != SwingEndReason.WallStop) _chains.Remove(rec.InstigatorId);

            _activeSwings.Remove(rec);
            if (_swingByInstigator.TryGetValue(rec.InstigatorId, out SwingRecord current) && current == rec)
            {
                _swingByInstigator.Remove(rec.InstigatorId);
            }

            _finishedSwingIds.Enqueue(rec.Id);
            while (_finishedSwingIds.Count > FinishedSwingMemory) _swingsById.Remove(_finishedSwingIds.Dequeue());
        }

        /// <summary>
        /// Effects of an accepted weapon hit on its target, decided here from weapon data and the resolved hit (D1b):
        /// a BROKEN guard staggers the defender (it cannot raise the guard for the tuning's stagger time, on this
        /// authority's clock), and the weapon's hit slow is put on the target through the ordinary status path
        /// (StatusComponent refreshes the same status and the strongest slow wins, so repeated hits never stack). A
        /// hit the guard fully blocked applies no slow; a broken guard does.
        /// </summary>
        private void ApplyHitEffects(ICombatEntity target, in DamageInfo damage, WeaponData weapon)
        {
            if (target == null || target.IsDead) return;

            if (damage.GuardBroken)
            {
                _guardBrokenUntil[target.CombatId] = Now + Mathf.Max(0f, FlowTuning.guardStaggerSeconds);
                GuardBreaksApplied++;
            }
            else if (damage.Blocked)
            {
                // The defender reacts to the blocked hit for a time set by how hard it was (weak / mid / heavy). A later, weaker
                // block never shortens a reaction that is already running.
                float resist = target is Emberdelve.Player.PlayerRig defender ? defender.Defense.ImpactResist : 0f;
                float until = Now + CombatFlowRules.BlockReactionSeconds(damage.ImpactPower, resist, FlowTuning);
                if (until > Now && (!_blockReactionUntil.TryGetValue(target.CombatId, out float running) || until > running))
                {
                    _blockReactionUntil[target.CombatId] = until;
                    BlockReactionsApplied++;
                }
            }

            StatusSpec slow = weapon != null ? weapon.HitSlowSpec : StatusSpec.None;
            if (!slow.IsNone && (!damage.Blocked || damage.GuardBroken) && target is IStatusTarget statusTarget)
            {
                statusTarget.ApplyStatus(slow, damage.SourceCombatId);
                HitSlowsApplied++;
            }
        }

        private static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
              || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }
}
