# Skeleton (Hollow Servitor) second attack: vertical overhead - 24 November 2026

Status: implemented and compiled in the working Editor; assets NOT regenerated (coordinator step below); visual review still needed.
Checkout: `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`. Scope: Hollow Servitor (visual variants Minion / Rogue, weapon `Claw`).
Ashen Warden (`ClawElite` / `MaulSweep`) and Cinder Warden are untouched (single pattern, club path, same windows).

## Behavior

- `Claw` now has two swing patterns: [0] `Pattern_ClawSlash` (the existing diagonal chop, values unchanged), [1] `Pattern_ClawOverhead` (new, `SwingKind.Overhead`).
- The server-side brain picks the attack per swing: `EnemyAgent` calls `EnemyAttackChoice.Pick(count, System.Random)` on the rising edge of the brain's windup (random 50/50, per-agent seed from tick count and combat id). The choice therefore exists during the telegraph, so the wedge, the red attack line, the animation and the sweep all show the blow that will really land. It is frozen into `_swingVariant` when `TryBeginAttack` submits the `MeleeSwingIntent` (its `PatternIndex` is the variant). The authority validates it through `WeaponData.TryResolveComboStep` -> `TryResolveSwingPattern` (index outside `swingPatterns` is rejected; a client never sends it).
- Telegraph: the ground wedge is rebuilt when the chosen attack differs from the built one (`BuildTelegraph`). Overhead = a narrow 24 degree strip along the committed facing (`EnemyAgent.TelegraphWedge`, because a vertical blade's yaw is meaningless: it reads 0..60 near vertical); diagonal keeps its pattern yaw range. `TelegraphReach`/`TelegraphHalfAngle`/`AttackPattern` follow the current attack.
- Animation: a new derived Humanoid clip `EnemyVisual_<Minion|Rogue>_Overhead.anim` (clip name `Overhead`, 1.2 s) is a second animator state `Overhead` (speed parameter `AttackSpeed`, exit to Idle). `EnemyAttackPhase` carries a variant: `OverheadSweepStart/End` 0.42..0.56, `OverheadRaiseEnd` 0.38, `OverheadChopLeadSeconds` 0.12, `OverheadRecoveryEnd` 0.92 (diagonal constants and Cinder club constants unchanged; `NormalizedTime(age, club, variant)`, `Recovery(status, club, variant)`). `EnemyVisualDriver` (server/host) and `NetEnemyViews` (client proxy) play `EnemyAttackPhase.StateName(variant)` and scrub it with the variant's window. A controller without the Overhead state falls back to the diagonal.
- Wire: `EnemyPresentationWire.AttackVariant` (byte, valid 0..`MaxAttackVariant`=1), `CurrentVersion` 4 -> 5, `NetTuning.ProtocolVersion` 47 -> 48. `NetCombatServer` fills it from `agent.AttackVariantIndex` and includes it in the change detection.
- Sweep: pattern 1 is baked by `EmberdelveEnemyVisualFactory.TryBakeSwing(..., attackVariant: 1)` from the overhead clip over its sweep window (13 keys of the held blade grip/direction, so the swept segment IS the visible blade). It shares the weapon's reach and sweep radius (set by the diagonal bake); only the inset (segment length) and timing differ (`EmberdelveAssetLibrary.BakeOverheadPattern`). It does not touch `enemy.attackRange` or `attackTelegraphCone`.
- Overhead pattern uses `SwingKind.Overhead` (the library gives Overhead patterns `impactPowerScale` 1.2, so a guard-break check is slightly easier for the overhead; damage is x1 like the diagonal). Defence code classifies by blade path (`BladeDefenseRules`) so blocking works as for the player's overhead.

## Dark and Darker reference (required by AGENTS.md)

Source root `C:/Users/yrapa/Downloads/DarkAndDarker-extracted`, package `pakchunk301-Windows/DungeonCrawler/Content/DungeonCrawler/Characters/Monster/Skeleton/SkeletonFootman/` (inspected by the coordinator).

Decoded facts (name tables only):
- `GameplayAbility/GA_SkeletonFootmanAttack1` -> montage `Animations/AM_SkeletonFootman_Attack3` (tag `Ability.Monster.Skill.One`).
- `GA_SkeletonFootmanAttack2` -> montage `AM_SkeletonFootman_Attack5` (`Skill.Two`, sections Attack01/Attack02, `Skill.Two.Next` follow-up).
- Both derive from `Monster/Common/GameplayAbility/GA_MonsterMeleeAttackBaseWithOptions`.
- Montages fire `Event.Attack.Collision.On/Off` and `Event.Attack.Hit` notifies: damage is a weapon-collision window inside each animation, per attack.
- `Data/Generated/DT_Monster/Monster/Id_Monster_SkeletonFootman_Common` lists both abilities with separate damage effects (`Id_MonsterEffect_SkeletonFootmanAttack1_Damage`, `..Attack2_Damage`).

Not decoded (inference / original design here): animation curves (which montage is vertical), selection logic (shared `Id_MonsterBehavior_Common` / BT), timings, damage numbers.

Adaptation (ours): two independent attack patterns, each with its own clip and its own swept collision window, chosen per swing by the server brain (the same shape as two abilities with their own montages and collision-on/off windows). The vertical swing shape, timings, 50/50 random selection and equal damage are our original design choice; the user explicitly requested a vertical overhead in addition to the diagonal.

## Animation approach (user-approved: procedural, no new third-party assets)

`Assets/Emberdelve/Editor/EmberdelveEnemyOverheadClip.cs` (editor-only, deterministic) builds the clip from the imported CC0 Quaternius library:
1. Windup: Idle (`Sword_Idle`) -> the raised pose of `Sword_Regular_A` at normalized 0.75 (blade up, over the head), 0..0.38, held to 0.42. Readable telegraph.
2. Strike 0.42..0.56: the right wrist follows an arc in the sagittal plane (polar angle about the shoulder from the raised pose to a straight arm 50 degrees below horizontal-forward), the blade pitches from about -69 (up) to +51 (down-forward) with its flat facing sideways, the spine/chest pitch forward (sign auto-detected from the head). It is solved per frame at 120 fps in muscle space (damped least squares / Levenberg-Marquardt through `HumanPoseHandler`, started from the previous frame so the humanoid twist decomposition stays continuous); an analytic two-bone solve was tried first and wrapped twists at the raised-arm singularity. Residual (wrist metres + blade/face direction) worst 0.010.
3. Recovery: hold to 0.62, blend back to the Idle pose by 0.92.
It is written as muscle + root curves of a normal Humanoid clip, so the host driver, client proxy and `TryBakeSwing` all use the same motion.

Baked overhead keys (Minion, 29-sample look; blade pitch positive down): grip from (-0.17, +0.12, -0.38) m relative to the eye to (0.05, -0.49, 0.59); blade pitch -69 -> 51 degrees, yaw about 0 at the end; farthest tip 1.66 m (diagonal 1.67), active window 0.25 s (0.14 clip fraction x 1.2 s x 1.5 stretch).

## Files changed

Runtime: `Runtime/AI/EnemyAttackPhase.cs`, `Runtime/AI/EnemyAttackChoice.cs` (new), `Runtime/AI/EnemyAgent.cs`, `Runtime/AI/EnemyVisualDriver.cs`, `Net/NetCombatWire.cs`, `Net/NetCombatServer.cs`, `Net/NetEnemyViews.cs`, `NetRules/NetLaunchArgs.cs` (protocol 48).
Editor: `Editor/EmberdelveEnemyOverheadClip.cs` (new), `Editor/EmberdelveEnemyVisualFactory.cs` (Variant.Overhead, clip path, controller state, `TryBakeSwing` attackVariant parameter, internal bake helpers), `Editor/EmberdelveAssetLibrary.cs` (`ClawOverhead`, `BakeOverheadPattern`, `AssignPatterns(Claw, slash, overhead)`), `Editor/EmberdelveEnemySweepGate.cs` (both patterns, overhead real-attack flow), `Editor/EmberdelveRuntimeSmokeTest.cs` (pins `EnemyAttackChoice.Override = 0`).
Tests: `Tests/EditMode/Combat/EnemyAttackVariantTests.cs` (new), `Tests/EditMode/NetInventory/NetEnemyPresentationWireTests.cs`, `Tests/EditMode/Visual/EnemyVisualQuaterniusTests.cs`.

## Coordinator: asset regeneration required

The committed assets are still the old ones (controllers have no Overhead state, `Claw` has one pattern, no `.anim`). Nothing was regenerated by this task.
Working Editor (Play mode stopped), the normal builder path does everything: menu `Emberdelve/Scenes/Build Modular Crypts Scene` (`EmberdelveLevelBuilder`: `EmberdelveAssetLibrary.BuildAll()` -> `ApplyEnemySwings` bakes `Pattern_ClawOverhead.asset` and assigns `Weapon_HollowClaw.swingPatterns = [Pattern_ClawSlash, Pattern_ClawOverhead]`; then `EmberdelveEnemyVisualFactory.EnsureAll` builds `EnemyVisual_Minion/Rogue_Overhead.anim`, the controllers with the Overhead state, the prefabs and the `Resources/Emberdelve/NetEnemyVisuals` copies). The pattern bake generates the clip in memory (deterministic), so the order of the two steps does not matter. On the mirror the equivalent is `EmberdelveEnemySwingReview.RebuildBatch` (`EnsureAll(true)` + `BuildAll()`).
After it: console should show `[Emberdelve][EnemySwing] claw_overhead: baked from Minion overhead clip ...`; `Pattern_ClawSlash` and `Pattern_MaulSweep` keys must be byte-identical (diff the .asset files). Then run `EmberdelveEnemySweepGate` (both patterns, overhead real-attack flow), the NetCombat enemy presentation gate, and the Visual EditMode fixtures, and do the motion review.

## Review findings and fixes (coordinator review)

1. Telegraph was destroyed/recreated per variant (deferred `Destroy` left the old wedge visible to the gate): now one `AttackTelegraphVisual`, `SetWedge(min, max)` redraws its mesh in place (no-op when unchanged).
2. Client probe logged only Attack/Hit/Death: `NetEnemyViews` now logs `state=Overhead`; `EmberdelveNetCombatGate` accepts `state=(Attack|Overhead)` for the attack-state check (other expectations unchanged).
3. Cinder Warden (`Pattern_CinderClub` is `SwingKind.Overhead`) was forced to +-12 degrees: the strip is now keyed off the Servitor attack variant 1 and never for `UsesCinderClub`; every other pattern uses its raw `YawRange` (the 24 degree widening was removed), so the Warden and Cinder Warden wedges are as before.
4. `EnemyAttackChoice.Override` leaks: reset in the sweep gate `finally`, the smoke test `OnDestroy`, and `[SetUp]`/`[TearDown]` of `EnemyAttackVariantTests`.
5. Selection now uses `EnemyAttackChoice.PickFor(weapon)`: only usable slots (non-null, `TryResolveComboStep(0, i)` succeeds, `Validate`); none usable -> 0. Unit test `ChoiceOnlyUsesUsablePatternSlots` (null slot).
6. `NetCombatServer.WireAttackVariant` no longer clamps silently: an unknown index is logged once as an error and sent as 0.
7. Overhead strip half angle is derived from the baked pattern (`EnemyAgent.OverheadStripHalfAngle`: widest lateral angle of the key blade tips in front of the eye + 6 degrees, clamped 12..40); gate uses `enemy.GetTelegraphWedge(variant)`.

## Mirror run 20261124-145421-832: new failures analysed

- SweptMelee "enemy claw has one swing pattern": expected by design; gate now asserts two claw patterns (pattern 0 diagonal `HorizontalRightToLeft`, pattern 1 `Overhead`, both valid) and the crossbow has none.
- SweptMelee "slash windup/recovery mirrors the weapon timing" (expected 0.24/0.30, got 0.28/0.34), "0.86 s: accepted", "production: chain step 3 accepted", "finisher reset the chain": NOT caused by this slice. They concern `Weapon_AshenShortSword` / `Pattern_SwordSlash*` (player data, no enemy involved). Committed HEAD data are inconsistent with the gate (weapon windup 0.24 / recovery 0.30 / cooldown 0.92, pattern windup 0.28 / recovery 0.34), and run 20261119-012844 (right after the 19 Nov data commit, before any enemy work) shows the identical five failures (725/5). The 22 Nov 729/0 pass ran on a working tree whose player data had been regenerated by `BuildAll` (uncommitted); reverting the Data to HEAD brought the stale values back. Fix needs a data decision (commit the regenerated player weapon/pattern data, or accept this baseline); no code change.
- Smoke "the windup resolved into a real hit (105 -> 105)": the player was already at 105 when the measurement started and the enemy's windup-end swing was refused as faster than the weapon cooldown, i.e. this enemy had already swung (and hit) once before the measured windup (baseline runs: 120 -> 105 inside the measured window). Not explained by the overhead: `Override = 0` pins pattern 0, the refused swing is the claw's, and nothing in this slice starts a swing. Most likely the enemy stayed enabled during the multi-frame lane search (layout-dependent). Hardened the fixture: the enemy is disabled while the lane is chosen (re-enabled where it already was) and a note is logged if the player is already hurt. If it still fails on rerun, the log line shows how many swings the enemy had begun.

### Smoke "windup resolved" follow-up (run 20261124-150753-427: 120 -> 120, refusal "from #3 (hollow_claw): faster than weapon cooldown")

Code evidence: (a) the only swing starter for an agent is `EnemyAgent.Update` -> `TryBeginAttack` on the brain's `RequestAttack` (EnemyAgent.cs, one call site); the slice adds only `PickAttackVariant` (a random draw on the windup edge, no begin), `ApplyTelegraph`/`SetWedge` (visual) and the pattern index in the intent. Nothing replays an order on re-enable (`Update` simply does not run while disabled). (b) The authority is untouched (`git diff` has no `LocalCombatAuthority*` change); the refusal is `now - lastAttack < FrozenRepeatInterval` = max(weapon.cooldown 2 x grace 0.8 = 1.6 s, windup+active+recovery 0.6 s) from the last accepted swing of that combat id. (c) Not observable from the old log (accepted swings are not logged).
Mechanism found in the fixture (present at baseline too, exposed by timing): `WakeStandingTarget` (smoke ~6010) calls `Brain.Tick(EnemyBrain.WakeSeconds = 1.25 s, ...)` once; for an already awake enemy that subtracts 1.25 s from the brain's own `_attackCooldownLeft` (EnemyBrain.cs:127) while the authority's clock is untouched. An enemy that swung shortly before therefore orders its next blow after about 2 - 1.25 + 0.6 = 1.35 s, inside the authority's 1.6 s floor, and is refused; whether it happens depends on how recently that enemy last swung (layout / enemy pick / frame pacing), which is why only some runs show it. Fix (fixture isolation, check unchanged): before re-enabling the frozen enemy the smoke now waits (`WaitForWeaponReady`, real time, authority's own cooldown clock) until the authority would accept its next swing. Permanent diagnostics added (`EnemyDiag` lines before re-enable, 3 frames after, at the measured windup and after the window: swingsBegun, last accepted attack time, cooldown remaining, chosen attack variant, Override, dt) so a further failure shows exactly which swing preceded it. Rerun the RuntimeSmokeTest combat flow once to confirm.

## Tests (working Editor, EditMode, MCP)

- `NetEnemyPresentationWireTests`: 24/24 passed (new: `AttackVariantSurvivesNetworkSerializationAndIsRangeChecked`, `OverheadAttackHasItsOwnClockWindowAndAnimatorState`).
- `EnemyAttackVariantTests` (new): 4/5 passed (authority rejects out-of-range / negative pattern index via `TryResolveComboStep`; choice stays in range and uses both attacks; single-attack monsters always 0; override seam). The 5th, `ServitorClawHasTheDiagonalAndTheOverheadPattern`, fails by design until the assets are regenerated (claw currently has 1 pattern).
- Not run: `EnemyVisualQuaterniusTests` (updated for the Overhead state; needs regenerated controllers), `EmberdelveEnemySweepGate` and the smoke test (coordinator, mirror).
- Compile: zero errors in the working Editor.
- Bake check (read-only, in memory): `TryBakeSwing("Minion", ..., attackVariant: 1)` returns 13 valid keys, weapon length 0.95 m, farthest tip 1.66 m, active 0.25 s; the LM arm solve worst residual 0.010.

## Open limitations

- No visual review done: the clip is analytically solved; look for the elbow/forearm twist, feet sliding between the raised pose and Idle, the blade edge orientation and whether the strike reads as a vertical chop from the front. Tunables: `EmberdelveEnemyOverheadClip` (`EndPitch`, `EndArmAngle`, `RecoveryStart`, lean 0.35) and the `EnemyAttackPhase.Overhead*` window constants.
- Rogue (female mannequin) shares the Minion's skeleton/avatar; the same clip plays on both.
- Asset-dependent tests (`ServitorClawHasTheDiagonalAndTheOverheadPattern`, the Quaternius controller state-count test) fail until the assets are regenerated.
- Pure thin clients do not own an `EnemyAgent`; they rely on the wire's `AttackVariant`. An old client against a new server is refused by the protocol bump.

## Motion review fix and asset regeneration (24 Nov, v2)

Review defects (body turned ~110 degrees side-on, cross-body windup sweep, wide recovery flourish) came from using `Sword_Regular_A` as the raised pose. `EmberdelveEnemyOverheadClip` now uses the Idle pose for the whole body (hips, legs, spine twist, left arm) in every frame and solves only the right arm (muscle-space LM IK) toward shoulder-relative targets: windup = Idle guard -> raised wrist (0.06, 0.30, 0.12) with blade pitch -70 (hand and blade come in over the body line first, then climb); strike = the sagittal arc to a straight arm 30 degrees below horizontal with blade pitch +60 and a forward spine/chest pitch (0.9); hold; recovery = straight line of wrist/blade back to the Idle guard. Timing constants in `EnemyAttackPhase` are unchanged (0.38 raise, 0.42..0.56 sweep, 0.92 recovery).
New entry `Emberdelve/Visual/Rebuild Servitor Overhead Attack` (`EmberdelveAssetLibrary.RebuildServitorOverhead`, `EmberdelveEnemyVisualFactory.EnsureOverheadVariants`): rebuilds only Minion/Rogue overhead clips, controllers, prefabs, Resources copies, `Pattern_ClawOverhead` and Claw's pattern list; no `BuildAll`.
Measured (Minion, preview scene, root space, tip = blade + 0.95 up): hips yaw deviation from idle max 1.9 deg, spine 5.4, chest 7.0. Windup tip |x| <= 0.6 from n=0.046 (the idle guard itself starts at x 0.77), <= 0.47 after n=0.06; tip y rises monotonically from 0.34 to 2.46 (no dip). Strike 0.42->0.56: tip (0.22, 2.45, 0.48) -> (0.08, 0.1..0.2, 1.0); farthest horizontal tip 1.44 m at n 0.51, x stays <= 0.22. Recovery tip y <= 0.34, |x| <= 0.77. Bake: farthest tip 1.44 m (was 1.66), active 0.25 s, 13 keys. Reach of the overhead is shorter than the diagonal (1.67): it hits a target about 0.2 m nearer. Contact sheet: scratchpad `overhead_strip_v2.png`.
Tests: EnemyAttackVariantTests 6/6, NetEnemyPresentationWireTests 24/24, EnemyVisualQuaterniusTests 44/46 (the two baseline failures).
