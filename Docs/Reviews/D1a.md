# D1a independent review: swept weapon melee, hit volumes, hit locations, wall stops

Reviewer: independent, clean context, read only. No edits other than this file, no git writes, no Unity runs.

Scope reviewed:
- `git diff HEAD` (23 files) plus the untracked files: `SwingPatternData`, `HitLocationTable`, `SwingGeometry`, `HitVolume`, `LocalCombatAuthority.Swings.cs`, `EmberdelveSweptMeleeGate.cs`, and the pattern and table assets.
- Docs: `Docs/Tasks/D1a.md`, the DND_PARITY rows, the UNITY_DESIGN combat line, R1a.md, AGENTS.md.
- Real callers: `ClassLoadout`, `PlayerLook`, `EnemyAgent`, `EmberdelveLevelBuilder` (enemy rig), `RunDirector`, `Equipment`.
- Logs: `Logs/d1a_final2_*`, `Logs/mut_*`, plus the older `r4r5_final_realcombat*.log` for comparison.
- The seven PNGs in `C:/temp/emberdelve/d1a/r720d/snapshots/`.

## Verdict: ACCEPT-WITH-FOLLOWUPS (M1-M3 must land before commit)

The core design holds up:
- The authority owns the swing. The intent carries no outcome data.
- The sweep is placed from the authority's own pose read and the pattern asset.
- The begin-time validation is complete and in a sensible order, with each rule pinned by a gate.
- The legacy `SubmitAttack` melee path is closed for the player, and the enemy claw path is untouched.
- Segment-vs-capsule math is correct, and wall clipping is applied before targets on every sample.
- The fixed sample grid makes results independent of the tick length (with one caveat, H2).
- 596 checks and the mutation runs show that the new gate is substantive.

Three problems block the commit:
1. A weapon swap does not cancel the authority's swing (M1). This regresses a property that R1a relied on.
2. The afterimage ribbon covers the aim line for most of every melee fight (M2).
3. The fixture changes and the gate leave two of the riskiest new behaviours untested (M3). The report also understates how much real-path rapier coverage was dropped.

None of the three fixes is large.

---

## Must fix before commit

### M1 (High, authority hole/regression): swapping weapons mid-swing leaves the old-weapon swing running and dealing damage
- `TryBeginMeleeSwing` checks the equipped weapon once, at begin (`LocalCombatAuthority.Swings.cs:125-131`). The record then keeps `Weapon = weapon` (`:179`).
- `StepSwing` (`:247-315`) re-checks registry membership and death, but never checks `IActiveWeaponProvider.ActiveWeapon == rec.Weapon`.
- `ClassLoadout.Update` allows a swap on any frame (`ClassLoadout.cs:110`). `Equip` re-initialises and disables `MeleeCombat` and destroys and rebuilds the visual (`:186-208`). Only the client is reset (`MeleeCombat.cs:73-84`, `OnDisable` `:249-253`). The authority's swing carries on.
- **Scenario:** an Archer starts a rapier thrust and presses swap during the 0.16 s windup.
  1. The crossbow comes up and the rapier visual is gone.
  2. About 0.1 s later, the authority sweeps an invisible rapier and damages whatever is in front.
  3. The same applies to a Warrior swapping sword to crossbow: an invisible 2.9 m sword slash lands with the crossbow in hand.
- The shared cooldown stamp (crossbow 1.1 x 0.8) stops an immediate double-dip today. But the hit itself violates "what you see is the real sweep" and the R1a property "a swap resets the swing, so a stale intent cannot be submitted after a swap" (R1a.md, Point 1, weapon-swap race). A future short-cooldown secondary would turn this into a damage stack.
- **Fix:**
  - In `StepSwing`, finish the swing with a new `SwingEndReason.WeaponChanged` and clear `Pending` when `(rec.Instigator as IActiveWeaponProvider)?.ActiveWeapon != rec.Weapon`.
  - Add a gate case: begin, switch `Swinger.Weapon` before the active phase, step, then expect no hit and the new end reason.
  - Optionally refuse a swap while `MeleeCombat.IsSwinging` (Dark and Darker cancels the attack on swap; either rule is fine as long as the authority enforces it).

### M2 (High, gameplay readability): the afterimage ribbon is an opaque, full-width bar on the crosshair line for 1.1 s after every swing
- `SwingTrail` holds the finished ribbon at full opacity for `HoldSeconds = 1.1f` (`WeaponVisualRig.cs:298, 396-399`). There is no fade.
- The material is the opaque glow material (`EmberdelveAssetLibrary.ApplySwingPatterns` assigns `PortalMat`; the runtime fallback `:327-331` is opaque as well).
- The ribbon spans the outer 35 % of a 2.44 m segment (`:40, :158`), so at 1.6-2.9 m from the eye it crosses the whole screen.
- The sword cooldown is 0.92 s, so the ribbon is visible almost all the time while you keep attacking.
- In the PNGs:
  - `b_mid_arc`, `i_hit1` and `i_hit3` each show a roughly 40 px solid amber band from edge to edge, exactly at crosshair height.
  - In `i_hit1` it cuts through the Servitor at the right edge, which is the band where you aim for heads.
  - In `i_hit3` it hides the corridor behind the crosshair.
- You cannot read an enemy's guard or head position, or a second enemy behind the first, through it. In a game where the head band matters (head x1.5), this works against the mechanic the slice adds.
- **Fix (cheap):**
  - Fade alpha to 0 over about 0.15-0.25 s after `EndStroke`, using a transparent or additive material.
  - Narrow the band to the outer 10-15 % of the blade, or a fixed 0.15 m.
  - Optionally fade along the ribbon from old to new.
  - Also consider hiding the `Sweep_Edge` bar outside the active phase. It is a debug-looking 2.4 m bar in `y1`.

### M3 (Test integrity): the riskiest new rules are barely pinned, and rapier real-path coverage dropped from 2 skipped cases to 14 without disclosure
- **Clip-and-continue is untested.** The moving-into-the-surface rule (`Swings.cs:342-346, 449-461`) is the new exploit surface: a blade touching a wall while moving along it or away from it is clipped but keeps sweeping. Only the clip stops a hit through the wall on those samples.
  - The mutation log confirms that removing clipping fails one check (`Logs/mut_M6_clipless.log`, the rapier thrust case), and that case stops anyway.
  - The side-wall case (`EmberdelveSweptMeleeGate.cs:824-842`) has no target behind the wall.
  - Add one: a target at about (-1.6, 1.5, 1.5) behind the left wall that the L->R slash starts against. It must take 0 damage while the swing completes and the front target is hit. Add the same for a floor-grazing low slash.
- **Weapon swap mid-swing:** see M1. There is no gate case today.
- **Real-path rapier matrix** (`EmberdelveRuntimeSmokeTest.cs:2059-2061, 2140-2148`):
  - The skip log grew from 2 lines to 14: compare `r4r5_final_realcombatarcher.log` with `d1a_final2_realcombatarcher.log`. 12 of the rapier's 22 expected-hit cases are now never asserted.
  - The +-25 deg column is generated as `ExpectHit = true` and is always skipped, because the lateral offset is already 0.38 m at 0.9 m. That is dead coverage that looks like coverage.
  - The 0.2 m "certain band" is very conservative. Torso radius 0.33 plus thrust sweep 0.10 means any lateral offset up to about 0.35 m at chest height is a certain body hit.
  - D1a.md says "no expectation was weakened" and "a thrust has fewer certain-hit cases" but never states the skip count.
- **Fix:**
  - Turn the +-25 deg cases into asserted misses (they are more than 0.5 m off the line at every distance).
  - Raise the hit band to about 0.3 m, or derive each case's expectation from `SwingGeometry` against the enemy's own `HitVolume`s.
  - Log the skip count as a check, e.g. "skipped <= N".
  - Update D1a.md with the numbers.

Everything else in the fixture changes is clean (see Point 4).

---

## Point 1: authority security

| Question | Finding |
|---|---|
| What does the client control? | The intent fields (`CombatContracts.cs` `MeleeSwingIntent`): id, weapon id, pattern index, combo step, aim snapshot, sequence. The gate pins the field list. No hit point, target, range, time or head flag. Closed. |
| Aim snapshot | It is only compared (12 deg, NaN/Inf refused, `Swings.cs:162-168`) and never used to place the sweep; the gate proves the authority's yaw wins (`:1245-1258`). **Locally the check is a no-op**: `MeleeCombat` sends `rig.TryGetAimPose`, and the authority reads the same camera through `PlayerRig.TryGetAimPose`, so both sides read one source. That is honest for an offline seam, but the "authoritative pose" IS the client camera. See H2 for the server consequence. |
| Mid-swing aim | The sweep follows the live pose every tick, interpolated (`:266-271, 284-287`), so a player can drag a swing with the mouse. That matches Dark and Darker, but the turn rate is unbounded (H2). |
| Aim spam / overlap | Sequence is checked first (`:142-146`, consumed on reject, as in R1a). Then "swing already in progress" (`:148`) and the cooldown on `Now` with the shared `_lastAttackTime` stamp (`:150-155`). Only an accepted begin stamps the cooldown (`:171`). One swing per instigator. Closed. |
| Cooldown grace | `cooldown x 0.8` lets a forged client swing every 0.736 s against 0.92 s for an honest one (25 % faster). This is a pre-existing R1a rule, but the authority now knows the swing length and could use `max(pattern.TotalTime + gap, cooldown x grace)`. (M-d) |
| Forged weapon/pattern/step | Equipped-weapon match is strict. Pattern resolved through `TryResolveSwingPattern` (`WeaponData.cs`); step != 0 refused; index out of range refused; pattern re-validated. Gate covers -1, 7, step 1 / -3, an enemy weapon id, the rapier with a sword index. Closed. |
| Replay | Same, older and 0 sequences refused, gaps allowed. Closed. |
| Death / vanish mid-swing | Swing ends with `InstigatorDied` / `InstigatorGone`; pending contacts dropped; nothing lands afterwards (`:253-264`, gated). Closed. |
| Weapon swap mid-swing | **Open (M1).** |
| Unknown ids | Unknown instigator refused. Unknown or dead targets are filtered in the sweep and re-resolved from the registry in `ApplySwingHit` (`:495-497`). Closed. |
| `AllowLegacyMeleeAttacks` | `internal`, default false (`Swings.cs:93`). There is no asmdef, so any runtime code in Assembly-CSharp could set it, but grep finds no runtime writer (only 5 Editor fixtures). Acceptable. Note: if the smoke combat block exits through `yield break` at `EmberdelveRuntimeSmokeTest.cs:515` (a failure path), the flag stays on for the rest of that run. Cosmetic. |
| Legacy refusal | `LocalCombatAuthority.cs:183` refuses a melee weapon from any `IAimPoseProvider` before target lookup, and does not spend a sequence. A forged 4 m sword `SubmitAttack` is gated as refused. R1a H2 (reach slack, no arc) is closed for the player. |
| Enemies | `EnemyAgent` only gained hit volumes. `SubmitAttack` with `target.CombatCenter` (the R1a M1 fix) and the wedge are unchanged, and the gate pins 15 claw damage. |
| Other damage paths | `SubmitSkill` and `Runtime/Skills/**` are untouched. Battle Cry keeps its flat damage. There are no melee-weapon skills (8 skill assets, all ranged, buff or flat). `RangedCombat` still uses `SubmitAttack` with the claimed-point/x1.8 rule (known open, R1a H1). |
| Guard while attacking | The guard flag is still client-owned (`PlayerRig.SetGuardHeld`). The authority accepts a swing while the player is "guarding", so a forged client can block and attack at once. This existed before, but the authority now owns swings and can refuse begin while `IsGuarding` and drop the guard while its swing runs. (M-c) |

## Point 2: sweep correctness

- **Segment distance** (`SwingGeometry.cs:99-151`): standard Ericson closest points. Both degenerate cases (point-point, point-segment) and the parallel case (`denom <= eps` gives s = 0, then t is clamped and s is recomputed) are handled correctly.
- **Sample grid** (`:53-76`): K is a pure function of the data, so for a static pose the result really is tick-independent. The gate proves this for six tick plans with a static `Swinger`.
- Two overclaims:
  - **The spacing guarantee holds only on average.** K = total travel / spacing, but samples are equal in time. For the shipped two-key patterns the motion is uniform, so average equals maximum. A D1b multi-key pattern with a fast segment will exceed "at most clamp(r/2)" (`:47-51` doc). Fix: take the maximum per-probe step x probes, or space samples by arc length. (M-b)
  - **Camera rotation is not in the grid.** A 180 deg flick inside one 20 ms tick spreads about 16 samples over 180 deg (about 0.57 m between samples at the tip). That is still finer than a torso, but it breaks the "no tunnelling" wording and makes results depend on tick boundaries. (H2)
- **Each target once:** the `Hit` set plus one pending contact per target, re-checked in `ApplySwingHit`. **maxTargets** counts hits plus pending, in sweep order. Correct. Minor: a contact whose damage turns out invalid has held a cleave slot.
- **Walls:**
  - The level-mask `Linecast` from base to tip clips the segment. `CheckSphere(base, r/2)` stops a buried base. Walls are tested before targets on the same sample, which is correct.
  - **Thin walls:** walls are 0.45 m thick (`DungeonLayoutData.wallThickness`). The sweep radius around a clip point is 0.16 m, plus at most 0.33 m of volume radius, so nothing behind a wall can be reached. That covers enemies, and players too (CharacterController radius 0.34).
  - **Corners and the eye-to-base gap (M-a):** the line from the eye to the blade base is never tested. At an outside corner, the base (up to 0.5 m from the eye, 0.25-0.30 m to the side) can sit on the far side of the corner edge while the eye is not. The blade then starts beyond the corner and can strike an enemy you cannot see. Fix: one extra `Linecast(eye, base)`, treated like a buried base.
  - **Moving-into rule:** sound in principle. It rescues the overhead, whose start pose points 2.4 m straight up, from ceilings, and slashes that start at a side wall. It is not exploitable as long as clipping stays (gate it, M3). Looking down about 45 deg makes slashes hit the floor and stop in their first half, which is Dark and Darker-like.
  - **Not tested against the wall mask:** the extraction gate pillars and chests are on `Emv_Interact` (`EmberdelveLevelBuilder.cs:511-524`), so swings pass through them. (Low)
- **Location rule (deepest volume within the window):**
  - Not client-exploitable: the client only controls aim.
  - It does not push toward head. The deepest rule favours whichever axis the blade passes closest to; the head/torso split for a level slash is at about 1.7 m blade height, the head's lower edge.
  - It does suppress "arm shielding": a slash touches the arm first, then reaches the torso within about 20 deg of travel, so the hit counts as Body. That is a deliberate deviation from first-touch. Keep it, but record it in DND_PARITY.
  - The real-path log shows plausible results: Arms (28) at point blank, Body (35) elsewhere.
- **Crouch:** the attacker's eye follows the camera, so crouch lowers the swing, which is good. Player volumes do not move with crouch, but nothing in production sweeps the player today (enemies use the legacy path).
- **Extreme pitch / NaN:** pitch is clamped to +-85 (`PlayerLook.cs:21-22`), so yaw from `Atan2` is well defined. `TryAimAngles` refuses zero and non-finite vectors, and an unreadable pose mid-swing falls back to the last pose. Fine.
- **Ordering and time:**
  - Begin runs in `Update` (`Time.time`); stepping runs in `FixedUpdate` (`fixedTime`, up to one fixed step behind). The first tick can see a slightly negative `ts`, which is harmless, and damage lands up to about 20 ms after the visual. This is consistent.
  - `timeScale 0` freezes both the client and the authority.
  - Run end or scene reload destroys the authority with its swing tables. The registry is static, but swing records hold only ids and references, and they are discarded.
- **Allocation:** the authority loop is allocation-free apart from a `Contact` per new contact. `SwingTrail.Rebuild` allocates new vertex and index arrays every frame of the active phase (`WeaponVisualRig.cs:372-373`). That is small but avoidable.
- `MeleeSwingStatus.Hits` hands out the record's live `List` (`Swings.cs:210-211`), so a caller can cast it and mutate it. Return a copy or a read-only wrapper. (Low)

## Point 3: hit volumes

- **Analytic capsules instead of Colliders:** a good call. No layer interference, it is server-runnable, and the gizmos help.
- **Sizing against the enemy blockout** (`EmberdelveLevelBuilder.cs:473-489`: body box y 0.225-1.675, +-0.39 x, +-0.29 z; head cube 1.63-2.07):
  - The head sphere (1.85, r 0.24) and the torso match well.
  - Arms reach out to +-0.53 m, past the 0.39 m visual body.
  - Feet (0-0.37 m) sit partly below the visual body, which starts at 0.225.
  - Acceptable for a blockout; retune when real models land.
  - The volumes stay on the root while the telegraph lifts `bodyTransform` (`EnemyAgent.cs:347`). Small mismatch.
  - Elite scale: the 1.25 factor is applied once. The root is not scaled, so there is no double scaling.
- **Does anything lack volumes?**
  - Every production `ICombatEntity` is a `PlayerRig` or an `EnemyAgent`. Both create volumes in `Awake` and lazily in `CollectHitShapes`.
  - Enemies are built by `EmberdelveLevelBuilder` into the scene with `EnemyAgent`, so they always get volumes at runtime. No scene change is needed, and no `.unity` file is in the diff. I agree.
  - An entity without `IHitShapeProvider` is silently unhittable by melee (`Swings.cs:370`). Add a one-line gate over `CombatRegistry` at Level01 start: every hostile entity provides at least one shape.
- **Melee weapons without patterns:** a player holding a melee weapon with no patterns can no longer attack at all. `MeleeCombat` logs and returns, and `SubmitAttack` refuses. No current player weapon is affected (the shield is off-hand only via `Equipment.cs:616-618`; the maul is enemy-only). Add a data gate: every class primary/secondary and every main-hand `grantedWeapon` that is melee has `UsesSweptMelee`. (Low)
- **Ranged:** still the claimed point on the head collider, x1.8. Correctly listed as open.

## Point 4: test integrity

Fixtures changed:

| Fixture | Change | Judgement |
|---|---|---|
| `EmberdelveCombatAuthorityGate` | New default-refusal check, then opt-in; 49 -> 50 checks | Clean. The refusal is asserted before the opt-in, and the other expectations are untouched. |
| `EmberdelveRuntimeSmokeTest` combat block | Same pattern, plus reset to false; 230 -> 231 checks | Clean. The removed `CollectMeleeHits` diagnostic was log-only. |
| `RealMeleeMatrix` (realcombat) | Sword offsets 0/20/42 deg instead of 0.35/0.7 x 60 deg (= 21/42). The old 3D-angle skip was removed. | **Stronger**: 0 skips, all 23 cases asserted, including point blank. |
| `RealMeleeMatrix` (realcombatarcher) | Thrust matrix, 0.2 m band, two new miss controls; 66 -> 64 checks | **Weaker**: 14 skips against 2 before, and a dead +-25 deg column (M3). |
| `EmberdelveAttributeSkillGate` | `TestTarget` gets one Body capsule | Clean. The numbers stay x1.0 by construction, and the 34.944 / 21.28 assertions are unchanged. |
| `EmberdelveAttributeActionGate`, `EmberdelveFlatDamageAffixGate` | Opt in to the legacy path | Acceptable. These are formula, range and rate probes of `SubmitAttack` itself. Note that their "sword on the real rig" now proves the formula, not the shipped melee path (the swept gate's real-rig section covers that). |

- **Swept gate strength:** it is substantive, not padding.
  - There are independent derivations of the pose and segment, a visual-vs-authority 2 mm comparison, and exact location multipliers.
  - Forged-intent checks each assert the rejection reason.
  - It has explicit negative controls (off-path thin target, behind, beyond reach, 20 deg off the thrust).
  - The mutation results (15/37/16/22/1/5 failures; logs present) show it bites.
- **Gaps in the gate:**
  - Clip-and-continue and weapon swap (M3).
  - A moving or turning attacker: the tick plans use a static pose, so the "1 ms .. 2.5 s identical" claim is only proven for a still camera.
  - The real-rig section calls `melee.TryBeginSwing` directly. Only realcombat/realcombatarcher press the real input.
- **Real input path:** yes. In realcombat/realcombatarcher, `PrimaryPressedOverride` -> `MeleeCombat.Update` -> `TryBeginMeleeSwing` -> `FixedUpdate StepSwings` -> `ApplyDamage`, with damage and `AcceptedCount` both asserted.
- **Final tree:** `Logs/d1a_final2_exit.txt` shows 37 entries plus SweptMelee, all exit 0, 0 `error CS`, 0 FAIL lines. The mutation logs were taken at 11:09-11:11, before the late flaky-fix edit to `MeleeCombat`. They are still indicative, because that edit touched only the client recoil timer.

## Point 5: feel and parity with Dark and Darker

- **Reach is too long (H1):**
  - The "Ashen **Short** Sword" reaches 2.9 m from the eye, along a 2.44 m damage segment from the hand.
  - In Dark and Darker a sword's reach comes from roughly 1 m of blade plus arm extension and body lean; a short sword connects at about 2.0-2.3 m from the camera, and longer weapons go further. (My estimate, not a measured value.)
  - 2.9 m also produces the corridor problem the worker saw: in a 3 m corridor most slashes touch a wall.
  - Recommend an effective reach of about 2.2-2.4 m for this sword (`reachScale` about 0.8) and a 0.9-1.2 m segment whose base travels outward along an arm arc. Then the visible model can be the damage segment and the 2.4 m glowing bar can go.
- **Speed:**
  - The slash sweeps 150 deg in 0.14 s, about 1070 deg/s. It is unreadable to a defender, and it makes the wall stop a binary "did a wall exist" check.
  - Dark and Darker's active arcs are visibly slower, with a shorter windup.
  - Recommend an active phase of about 0.25-0.35 s and a narrower yaw range (+-50-60 deg) for a short sword. Do this in D1b together with the move-speed penalty (`moveSpeedMultiplier` is a hook nobody reads yet, so the player swings at full run speed, which feels off).
- **Overhead on sprint + attack:** a placeholder that hijacks the sprint key. Acceptable until D1b combos, but say so in the HUD or tooltip, or bind it to an explicit alt-attack.
- **Rapier thrust:** 0.10 sweep, plus or minus 0.43 m body coverage. That is genuinely line-like and right for a thrust, but punishing with no slash fallback. D1b should give the rapier a slash step.
- **Wall stop feedback:** only the truncated ribbon and the recoil animation. Add an impact spark and sound at the contact point and a short camera kick. The bounce is the signature mechanic and should be unmistakable.
- **Visual vs damage at walls:** the visual blade and ribbon are not clipped at the wall, while the damage segment is. The difference is mostly hidden by depth, but it is a small divergence from the design rule "visible trajectory matching the real sweep". (Low)

## Point 6: visual review (what I actually saw)

- `b_mid_arc`: a solid amber band, about 40 px tall, crosses the full 1280 px width at crosshair height. The sword is at bottom right, rendered as a large, nearly white, unshaded slab. The bloom or overexposure looks wrong, but it is a blockout. See M2.
- `i_hit1`: the red damage vignette, the "Hit -15 HP" toast, the red X hit marker over the ribbon, and the Servitor at the right edge with the band across it. The hit marker is readable; the enemy is not. No hit location is shown. A headshot cue would help sell the new table (Low).
- `i_hit3`: "Hollow Servitor slain", "Slain: 1", and the "Take Blood Draught" prompt. The ribbon is again opaque and edge to edge. Nothing in the HUD is broken.
- `y1_wall_partial_arc`: a short amber stub at the right (x about 965-1280) and the sword mid-recoil with a thin yellow `Sweep_Edge` bar pointing up. It reads as "something stopped", but the wall contact point is not marked, and the stub floats in front of the wall. The blade slab at bottom right washes out the "Stamina" HUD label (white behind white text).
- `h0` / `h1`: rest pose with the sword pointing up-left from the bottom right. Sane. The ability cards overlap the weapon area at bottom right (pre-existing layout).
- **What I cannot confirm:**
  - Motion: smoothness, the ribbon lifetime in practice, recoil easing, the frame-level alignment of the visual with the damage.
  - Any frame where a head hit occurred.
  - Whether the left-bottom orange box in `b_mid_arc` is a world prop or a stray visual. It looks like a world object (the drop cube appears in the same place in `i_hit3`).

## Point 7: compile/API, dead code, overclaims

- **Compile:** errCS = 0 on the final tree.
  - `ICombatAuthority` gained two members; the only implementer is `LocalCombatAuthority`.
  - `MeleeCombat.AttackStarted` changed its type; `FeedbackDirector` and the smoke tests were updated.
  - `DamageInfo` gained an optional last parameter, so source-compatible.
- **Dead code:**
  - `HitScanner.CollectMeleeHits` was removed and has no callers left.
  - `WeaponData.arcAngle` and `windupTime/activeWindow/recoveryTime` remain. The timings are now duplicated into each pattern by the builder, which is two sources of truth: the lobby card or HUD may show weapon timings that differ from a tuned pattern (the overhead already does). Make the pattern authoritative and derive the display from it.
  - `HitLocationTable.Multiplier(headBonus)` is a hook with no caller yet. Fine.
- **Overclaims in D1a.md:**
  - "the result does not depend on the tick length": true only for a static pose (H2).
  - "no hit volume can fall between two samples": the spacing is an average, and rotation is ignored (M-b, H2).
  - "no expectation was weakened": the rapier real-path matrix lost 12 asserted cases to skips (M3).
  - "the material is EmberGate_Glow": the builder assigns `EmberdelveMaterials.PortalMat`. That is probably the same asset; check the name.
  - Everything else I checked matched the code: field list, validation order, layouts, numbers, check counts, logs.

---

## Ranked follow-ups (after the must-fix items)

1. **H1 Reach and speed tuning (feel):** about 2.2-2.4 m effective reach, a 0.9-1.2 m segment, a 0.25-0.35 s active phase, narrower slash yaw. Retune after M2 so you can see the result.
2. **H2 Turn rate and networking:** cap the per-tick yaw/pitch delta the sweep accepts during the active phase (e.g. 720 deg/s), and add samples by angular delta when the frame rotates, so the no-tunnelling claim holds while turning. Pin the cap with a gate (a 180 deg flick in one tick must not hit a target behind). On a real server this is the main melee cheat surface, because the "authoritative pose" is client input.
3. **M-a Eye-to-base wall test:** one `Linecast(eye, base)`, treated as a buried base. Gate it with an outside-corner case.
4. **M-b Arc-length sample spacing:** needed before D1b multi-key patterns.
5. **M-c Server-side guard/attack exclusion:** refuse begin while guarding, and drop the guard while a swing runs.
6. **M-d Cooldown floor:** `max(TotalTime, cooldown x grace)` on the authority.
7. **Data and health gates:** every hostile entity exposes hit shapes; every player-usable melee weapon has patterns.
8. **Low:**
   - Interact-layer props are not swing blockers.
   - Volumes do not follow the telegraph body lift or crouch.
   - `MeleeSwingStatus.Hits` leaks the live list.
   - `SwingTrail` allocates every frame.
   - The visual blade is not clipped at walls.
   - No wall-impact VFX/SFX.
   - Document the deepest-volume location rule as a deliberate deviation in DND_PARITY.
