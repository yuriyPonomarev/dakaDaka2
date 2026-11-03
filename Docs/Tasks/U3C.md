# U3C1 - committed blade trajectory and directional height / endurance defense (authoritative rules, offline + server path)

Status: implemented, NOT compiled or run by the author (no Unity available). The local tester must compile and run everything below. U3C is split: this is U3C1 (rules, authority, offline and the U3B3 swing event). U3C2 (rendered warning, live play-mode proof, PNGs, defaults) is listed at the end.

## Read this first: the stamina conflict

The roadmap row and `UNITY_DESIGN.md` say "stamina defense / empty stamina". The recorded user decision (PLAN_CLAUDE.md "asked and answered", `DND_PARITY.md` C2, `Docs/Tasks/D1b.md`) is the opposite: stamina was REMOVED from block and sprint (B3), and `EmberdelveCombatFlowGate.NoStaminaSource` fails the build if any runtime code or member names it. So U3C1 delivers the mechanism the row asks for under another name and keeps the decision:

- The pool is called guard ENDURANCE (`GuardEnduranceState`, `CombatFlowTuning.guardEndurance*`). No stamina bar, attribute, HUD row or sprint cost returns.
- It is OFF in the shipped tuning (`guardEnduranceMax = 0`): blocking stays free, exactly the D1b/C2 behaviour. Every empty-pool rule is implemented, unit-tested, gate-tested and live-rig-tested with a tuning that enables it.
- To turn it on for the game: set `guardEnduranceMax` (and the cost/regen numbers) on `EmberdelveGameConfig.asset` `flow`, then flip the two default assertions in `EmberdelveBladeDefenseGate.DataSection` (they pin "off"). That is a product decision; ask the user before doing it.

## Design

One path, three consumers. A swing's path is `SwingGeometry.PathSegment(frame, pattern, bladeLength, u)` (= pattern keys -> pose -> world segment). It is called by:
1. the authority's sweep (`LocalCombatAuthority.ProcessSample`, unchanged math, now through this function);
2. `BladeWarning.SegmentAt` / `Touches`, the committed-trajectory warning the authority builds when a swing begins (`SwingRecord.Warning`, exposed as `MeleeSwingStatus.Warning`);
3. the gate's reference computation.
The contact test is `SwingGeometry.SweepDepth` (capsule radius + sweep radius - segment distance), used by the sweep and by `BladeWarning.Touches`.

What a swing commits to at begin: pattern (so kind: horizontal / overhead / thrust), weapon, sample grid, aim yaw/pitch, eye. `CombatFlowTuning.commitSwingAim` (default false) makes the sweep keep that aim for the whole swing (the eye still follows the body); false keeps the D1c2 behaviour (follower of the live aim at the turn ceiling), where the warning is the prediction for the aim at begin.

Defense is decided by the pure function `BladeGuardRules.Resolve(GuardInput) -> GuardVerdict` (Rules assembly, no UnityEngine), fed by `PlayerRig.TryResolveGuard` on the authority side (offline, host, and the server rig of a remote player - the same code the NetCombatServer drives). The attack height band is computed by the authority from the pattern (`DamageResolver.ResolveSwing` -> `DamageInfo.AttackHeight`), never from an intent.

### Rules table (checked in this order)

| # | Condition | Outcome | Damage | Endurance |
|---|---|---|---|---|
| 1 | guard not up / kit cannot block / dead / staggered / own swing running | NotGuarding | full | none |
| 2 | bearing to attacker > arc / 2 (behind, or outside the kit's arc) | OutsideArc | full | none |
| 3 | attack band not covered by guard band (|band difference| > tolerance) | WrongHeight | full | none |
| 4 | endurance enabled and pool empty or below the cost | Exhausted | full | none |
| 5 | impact power > 0 and >= kit impact resist | Broken (stagger by the authority as before) | reduced by reduction x breakScale | pays cost |
| 6 | otherwise | Blocked | reduced by kit reduction | pays cost |

- Attack band: from the swing's trajectory through the tuning table `horizontalHeight / overheadHeight / thrustHeight` (defaults Mid / High / Mid). Arrows, skills and legacy hits have no band (Unspecified = always covered).
- Guard band: from the defender's view pitch (positive down): pitch <= -guardHighPitchDegrees -> High, pitch >= guardLowPitchDegrees -> Low, else Mid (25 / 25). A mid guard covers everything.
- Tolerance: blade kit `bladeHeightTolerance` = 1 (a Low guard cannot stop an overhead; Mid covers all), shield kit `shieldHeightTolerance` = 2 (a shield covers every band).
- Cost of a held hit: `guardBlockCostBase + impactPower x guardBlockCostPerImpact` (10 + 0.5 x impact), charged exactly once per resolved hit (a swing hits a target once; one `TryResolveGuard` per hit). Regeneration starts `guardEnduranceRegenDelay` after the last payment at `guardEnduranceRegenPerSecond`, computed from elapsed authority time, so it does not depend on tick size.
- Exhausted gives no stagger by default (no new break rule); the player simply cannot block until the pool refills.
- Shields never parry: `DefenseProfile.Sanitize` (unchanged) strips Parry from a shield; `BladeGuardRules.CanParry(hasShield, parryCapability)`; the verdict has no parry/riposte member and no runtime code consumes the Parry capability (the gate scans). Parry stays a reserved weapon capability.
- Evasion: the repository has no dodge or i-frame concept (Dash is movement only). Evasion is therefore geometric: the sweep only touches bodies inside the swept volume, so a target that has left it is missed. With `commitSwingAim` the volume cannot follow the target; with the default tuning the aim may still turn at up to the D1c2 ceiling (720 deg/s), documented limitation until a product decision turns commit on.

## Presentation event (U3B3 carrier)

`CombatSwingWire` now also carries trajectory, height band, committed flag, committed aim yaw/pitch, blade yaw/pitch extents and reach, all copied by the server from the authority's `MeleeSwingStatus.Warning` (`NetCombatServer.OnSwingStarted`, `CombatSwingWire.SetWarning`). The `combat-swing` and `[client] server-swing` log lines gained trailing `key=value` fields (`trajectory height yaw pitch minyaw maxyaw reach committed`); the existing gate greps match prefixes and are unaffected. A client draws nothing new yet (U3C2); it receives only server data.

## Decisions

1. Endurance is real code but off by default, named endurance, to respect the recorded C2 decision (see top). Flagged for the user.
2. The height rule is ON by default (tolerance 1 / 2): it changes outcomes only for a blade guard held with the view pitched >= 25 degrees down against an overhead swing.
3. Guard height follows view pitch rather than a separate stance key: needs no new input, no wire intent, and the server already has the pitch.
4. `commitSwingAim` defaults OFF because the local first-person weapon visual follows the camera; committing the sweep without committing the visual would make the visible blade differ from the real sweep (the D1a invariant). Visual commit belongs to U3C2.
5. One pure rule file (`BladeDefenseRules.cs`) with a state-free `Resolve` and a clock-injected pool, so the rig, the gate and the unit tests share one implementation. `CombatFlowRules.IsGuardBroken` now delegates to it (same result).
6. Expected values in the gate come from assets and tuning (pattern keys, kit numbers, tuning table, weapon impact); the unit tests derive expectations from their own inputs.

## Intentional behaviour changes (everything else is meant to be identical)

1. Blade guard held while looking down >= `guardLowPitchDegrees` (25) against an overhead pattern swing (sword overhead = combo step 3; any enemy pattern authored Overhead): no longer blocked (WrongHeight). Shield kits are unaffected. Before: blocked whenever inside the arc.
2. `DamageInfo` has a new trailing optional field `AttackHeight` (copied by every `With*` / armor / falloff copy).
3. `CombatSwingWire` is larger and the swing log lines have extra trailing fields (all builds must match, as with any wire change).
4. `PlayerRig.LastGuardOutcome`, `GuardEnduranceCurrent`, `CurrentGuardHeight(...)` added. Nothing else on the rig changed shape.

## Older gates and tests whose expectations this could touch

- `EmberdelveCombatFlowGate` (touched on purpose: new section "U3C rig" `BladeDefenseOnRig`, check count rises; every older guard section uses a level view, so Mid; a failure in an OLD section there is a regression).
- `EmberdelveNetCombatGate` (melee-duel: B guards at level pitch against the sword chain including the overhead; must stay blocked-with-reduction; log lines grew), `EmberdelveNetProofGate`, `EmberdelveNetFinalGate`, `EmberdelveNetSeamsGate` (compile + wire).
- `EmberdelveSweptMeleeGate`, `EmberdelveEnemySweepGate` (sweep refactor `PathSegment` / `SweepDepth`: identical float expressions; fake defenders).
- `EmberdelveCombatAuthorityGate`, `EmberdelveProjectileBlockGate`, `EmberdelveSkillAuthorityGate`, `EmberdelveAttributeSkillGate`, `EmberdelveFlatDamageAffixGate`, `EmberdelveArmorAffixGate`, `EmberdelveRuntimeSmokeTest` (they use `DamageInfo`, the rig guard and projectile blocks; arrows/skills carry no band).
- EditMode: all earlier fixtures plus the new `BladeDefenseRulesTests` (Rules assembly).

## Risks, most likely first

1. Compile errors: nothing was compiled. Higher-risk spots: `NetCombatWire.cs` (new usings, `in` parameter on a struct method), `NetCombatServer.OnSwingStarted` (out vars in an `if` chain), `PlayerRig.TryResolveGuard` rewrite (CRLF file, struct initializer), the gate (`in` arguments given rvalues, `out _`). Fallbacks: drop `in` modifiers; use plain locals.
2. Policy: the stamina/endurance default (above). A reviewer may read "emptystamina" acceptance as not met by default gameplay; it is proven only with the pool enabled in tuning.
3. The new serialized fields on `CombatFlowTuning` rely on Unity's field-initializer defaults when `EmberdelveGameConfig.asset` predates them (same assumption D1b made). If the asset reads them as 0 / Unspecified, `EmberdelveBladeDefenseGate.DataSection` fails ("every trajectory has a height band") and the height rule is a no-op; fix by re-saving the config through the builder.
4. `BladeDefenseOnRig` (inside the CombatFlow harness) assumes the rig's view pitch is level and the authority clock is the gate's injected clock; if a previous section left the rig pitched or staggered, it will say so in its labels.
5. Warning == sweep is proven structurally and geometrically (same functions, sampled segments compared, contact test shared). It is NOT proven live: there is no play-mode scenario that moves a target during a committed swing, and `commitSwingAim = true` has no live test.
6. Height thresholds (25 degrees) are untuned; a player looking down at a nearby enemy may lose overhead blocks with a sword (shields are immune).
7. Observer rendering of the warning does not exist yet; the wire fields are logged only.

## Tester commands

- Compile, then EditMode (default filter runs the whole Rules namespace, including the new fixture): `Tools\Run-EmberdelveEditModeTests.ps1 -ProjectPath <project>`; new fixture alone: add `-TestFilter Emberdelve.Rules.Tests.BladeDefenseRulesTests`.
- New gate (edit mode, no play mode): `Tools\Run-EmberdelveGates.ps1 -ProjectPath <project> -Methods 'Emberdelve.EditorTools.EmberdelveBladeDefenseGate.Run'`. Last line: `[Emberdelve][BladeDefense] PASS checks=N failures=0`.
- Live rig sections: `-Methods 'Emberdelve.EditorTools.EmberdelveCombatFlowGate.Run'` (look for the `U3C rig` labels).
- Regression reruns: `EmberdelveSweptMeleeGate.Run`, `EmberdelveEnemySweepGate.Run`, `EmberdelveCombatAuthorityGate.Run`, `EmberdelveProjectileBlockGate.Run`, `EmberdelveSkillAuthorityGate.Run`, `EmberdelveAttributeSkillGate.Run`, `EmberdelveNetCombatGate.Run`, then the net family from U3B3 (`EmberdelveNetSkillGate`, `EmberdelveNetAdmissionGate`, `EmberdelveNetMovementGate`, `EmberdelveNetSeamsGate`, `EmberdelveNetProofGate`, `EmberdelveNetFinalGate`).

## What the new gate covers (EmberdelveBladeDefenseGate)

data (every authored pattern validates; authored kind == class measured from its keys; horizontal, overhead and thrust all present; shipped tuning pins), warning equals sweep (per pattern: sample grid, trajectory, band, yaw range == the enemy wedge's, reach, every sampled segment equal to the sweep's, end samples equal to the raw keys, body-following, shape of each class, on-path body inside warning and sweep contact), evasion (radial step-out missed for every pattern, attacker walking away, thrust sidestep derived from the strip width), front/back/height (arc edge, one degree past, 180, mid guard covers all, lowered blade guard vs overhead = WrongHeight, shield covers), endurance (exactly floor(max/cost) held, then Exhausted, payment exactly once, delay + regen, back/free cases), resolver (band travels in `DamageInfo` through every copy; blocked / behind / wrong-height / maul-break damage numbers derived from kit and tuning), shields never parry (asset, forged Parry bit, profile, pure rule, source scan), wire (event fields equal the warning).

## U3C2 - what remains

- Play-mode authority scenario on an injected clock: commit on, target moving out of the volume during windup (miss) vs staying (hit); live comparison of `Warning.Touches` to the actual `MeleeSwingHit` list per pattern.
- Rendered victim warning (ground wedge plus height band, from `BladeWarning` / the wire fields; replace the YawRange-only enemy wedge with the vertical extent for overhead), observer-side drawing from `CombatSwingWire`, real windowed client PNGs.
- Commit the first-person weapon visual to the committed aim, then decide whether `commitSwingAim` ships on.
- Product decision on endurance default (and a HUD pip if it ships), optional guard-break on exhaustion, attacker/defender elevation difference, tuning of the pitch thresholds.
- Networked scenario: forged claims cannot change band or committed aim (they are not in any intent today), and an overhead against a looking-down remote defender.

## FIX1 - decision: a sword guard holds against sword hits (product owner, 2026-11-03)

- Decision (Dark and Darker behaviour): a one-handed sword blade guard HOLDS against sword hits; heavy weapons (the warden maul, impact 30) still break it. Before this, the sword's own impact power (10, overhead 10 x 1.2 = 12) reached the blade kit's impact resist (8) and every sword-vs-sword block was a guard break (reduction x 0.3).
- Authored data, not a rule change: `Weapon_AshenShortSword.impactResist` 8 -> 15 (`EmberdelveAssetLibrary.ApplyCombatFlow`, a plain assignment that runs on every `BuildAll`, so existing assets are updated; the committed `.asset` carries the same value). 15 is above the strongest sword pattern (12) and below the maul (30). The rapier (resist 5), the claw (impact 12 now holds against a blade-only sword guard too) and every shield (heater 20) are unchanged. `BladeGuardRules.IsGuardBroken` (power >= resist) is unchanged.
- `EmberdelveBladeDefenseGate` (front/back/height): the blade kit's resist equals the sword's authored resist; every sword pattern is held with the full kit reduction and is NOT a break; every maul pattern still breaks the blade guard with reduction x `guardBreakReductionScale`. All numbers are derived from the assets and the shipped tuning. The existing "held with the kit's reduction" check keeps meaning "the full kit reduction".
- `EmberdelveCombatFlowGate` buckler fixture: the fixture's resist is now `sword.impactResist + 1` (it was a literal 12, which the kit takes the max of with the sword's resist); the intent (sword blocks it, heavy breaks it) is unchanged.
