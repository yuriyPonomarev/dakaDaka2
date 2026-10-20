# R1a independent review: weapon attacks no longer trust client data

Reviewer: independent, clean context, read only (no edits, no git writes, no Unity runs).
Scope reviewed: `git diff HEAD` (10 files), the untracked `Assets/Emberdelve/Editor/EmberdelveCombatAuthorityGate.cs`, `Docs/Tasks/R1a.md`, `AGENTS.md`, and F1/F2 in `Docs/Reviews/CODEBASE_HEALTH_20261020.md`. I also read the real callers (`MeleeCombat`, `RangedCombat`, `EnemyAgent`, `EnemyBrain`, `ClassLoadout`, `HitScanner`), the weapon/enemy data assets, the enemy builder and the `Logs/r1a_*` results.

## Verdict: ACCEPT-WITH-FOLLOWUPS (M1 must land before commit)

The authority change is sound. On `SubmitAttack`, the weapon, `MaxRange`, `Timestamp` and the headshot *flag* are really neutralised, and the hit point is bounded on the real code path. No path bypasses it inside `SubmitAttack`. The fixture rewrites keep the original assertions; I found no expected value changed to make a test pass. The new gate would fail on the old authority (trace below).

One real gameplay regression blocks the commit: enemy attacks that the telegraph promises now get silently rejected (M1). The fix is small. There is also one verification gap to close (M2). The other findings are follow-ups.

The headshot row of F1 is **converted, not closed**. A client that picks a point inside the head collider still gets x1.8 every time. That is acceptable for this local seam, but only if it is recorded as open for U3 (see H1).

---

## Must fix before commit

### M1 (High, behaviour regression): an off-axis enemy hit inside the warned wedge is now rejected
- `EnemyAgent.cs:360` builds `aimPoint = origin + warned * distance + up*dy`. This point sits on the direction committed at windup start (`EnemyBrain.cs:233`), at the player's distance. It is **not** on the player.
- `LocalCombatAuthority.cs:173-176` now rejects any claimed point more than 1.5 m from the target's `CombatCenter`.
- The gap between the two points is the chord `2·d·sin(θ/2)`, where θ is the angle between the warned direction and the player. The default `attackTelegraphCone` is 120° (neither enemy asset overrides it), so the half-cone is 60°. `maxRange` is 2.6 m for the Servitor and 2.9 m for the Warden. The hit is rejected whenever `d·sin(θ/2) > 0.75`. Examples:
  - 2.0 m at 45° off-axis
  - 2.5 m at 35°
  - 1.6 m at 56°
- **Scenario:** a Hollow Servitor commits its swing while the player is straight ahead at 2 m. During the 0.6 s windup the player strafes 1.5 m sideways and ends about 45° off-axis, still inside the drawn wedge and still in range. `EnemyAgent` submits. The authority logs "claimed hit point is not on the target" and applies no damage. `LastAttackWhiffed` stays false, so no whiff visual plays. The brain has already spent its own cooldown.
  - Before R1a this hit landed: the old check was Origin→HitPoint ≤ MaxRange.
  - This breaks the design rule written on `EnemyData.attackTelegraphCone`: "what the telegraph shows is what actually lands".
  - It makes every melee enemy weaker against strafing, which is the most common player movement.
- **Why the tests miss it:** every enemy fixture puts the player on the warned axis. The smoke telegraph strike (`EmberdelveRuntimeSmokeTest.cs:712-729`) has a stationary player. Guard, death and measured-guard all submit `_rig.CombatCenter`. No test covers an off-axis hit inside the wedge.
- **Fix:** `EnemyAgent` is authority-side AI and has already done the wedge test, so it should submit a point on the target, e.g. `target.CombatCenter`. Keep the warned direction only for the wedge decision. Add a fixture: put the player 45° off the committed direction at 2.2 m inside the wedge and expect the hit to be accepted with 15 damage. Add a second one outside the wedge and expect `LastAttackWhiffed`.
  - Note: `DamageInfo.Direction` for enemy hits is now "toward the target centre" (`LocalCombatAuthority.cs:197`), not the warned direction. The check at smoke `:723-728` ("hit arrived along the warned direction") still passes only because the player stands on the axis. Re-word that check or pin the new meaning.

### M2 (Verification): re-run the non-combat smoke modes on the final tree
`EmberdelveRuntimeSmokeTest.cs` was last written at 21:14:57. `ai`, `archer`, `death`, `extraction`, `resultloop`, `lobby`, `items` and `inventoryui` ran from 21:05 to 21:08 against the earlier file. Only `combat` ran after the edit. The report says the edit only touched the combat block, but the same file holds the shared `WaitForWeaponReady` helper and all the modes. Re-running is cheap. AGENTS.md says not to claim checks that were not executed on the final tree. Re-run them after M1 as well.

---

## Point 1: is each forged field neutralised on the real path?

| Field | Real path (PlayerRig melee / ranged, EnemyAgent) | Status |
|---|---|---|
| Weapon | `:127-133`: resolved from `IActiveWeaponProvider`. PlayerRig returns `Loadout.ActiveWeapon` and EnemyAgent returns `data.attackWeapon`. Damage, reach, cooldown and `Finalize` (`DamageAndScanning.cs:37`) all use the resolved weapon. Mismatched `WeaponId` or object → reject. An entity without the provider → reject. | Closed |
| MaxRange | `:160-164`: a claim above weapon reach + 0.8 is rejected. Otherwise it is never used: reach comes from the weapon. A NaN claim fails the `!(x <= y)` form. | Closed (see L3 for fragility) |
| Hit point | `:172-184`: must lie within 1.5 m of the target centre, then is clamped into a 0.5 m sphere. Distance is measured from the instigator's own `CombatCenter`. NaN/Inf → reject. | Bounded (see H2 for residual slack) |
| Headshot flag | `:193-195`: the flag is ignored and the head collider geometry at the claimed point decides. | Flag closed. Point-choice headshot still open (H1) |
| Timestamp | `:152-157`, `:211`: authority clock (`Now`), and only an accepted attack spends the cooldown. | Closed |
| Origin / Direction | Origin is only sanity-checked (4 m). Direction is derived from positions. | Closed |

**Bypasses checked:**
- Only `PlayerRig` and `EnemyAgent` implement `ICombatEntity`, and both implement the new interface. No runtime code calls `ApplyDamage` on an entity directly except the authority (`:213`, `:297`). The entities' own `ApplyDamage` forwards to health.
- `RangedCombat` and `MeleeCombat` do not resolve damage themselves. `HitScanner` only proposes candidates.
- Weapon-swap race: `ClassLoadout.Equip` (`ClassLoadout.cs:179-198`) re-`Initialize`s `MeleeCombat`, which resets the swing to Idle, so a stale `_pendingIntent` cannot be submitted after a swap. A ranged swap disables `RangedCombat`. Locally there is no race. Under networking a legitimate in-flight shot fired just before a swap will be rejected; U3 needs an equipment-change sequence (L5).
- **The real remaining bypass is `SubmitSkill` (`:226-320`).** It still trusts `Weapon`, `MaxRange`, `HitPoint`, the `Headshot` flag, `Timestamp`, `ActivationId` and `EffectScale`. A forging client can send its attack as a "skill" and skip all of R1a. This is explicitly R1b scope and documented in R1a.md. It means `ICombatAuthority` as a whole is **not** in server-authority shape until R1b lands. It does not block R1a, but nobody should cite R1a alone as closing F1.

### H1 (Open by design, record for U3): a client-chosen point inside the head collider is still a free headshot
`(head.ClosestPoint(p) - p).sqrMagnitude <= ε²` is true for **every point inside** the box/sphere, because `ClosestPoint` returns `p` itself.
- The enemy head is a 0.44 m cube centred 0.70 m above `CombatCenter` (`EmberdelveLevelBuilder.cs:485-499`). The elite head is 1.16 m above it. Both are inside the 1.5 m tolerance.
- A malicious client that always claims the head centre gets x1.8 on every accepted hit, which is the same power as the old flag.
- The authority also ignores `intent.Direction`, so the claimed point does not even have to lie on the aim ray. Line of sight is checked only to the body centre, not to the claimed point.

For a local, offline seam this is acceptable. Honest clients now get geometry-consistent headshots, and a flag that contradicts geometry can no longer win. R1a.md says "aim is client input", which is honest, but it should say plainly that **the F1 headshot row stays open**.

**What U3 must do:** the server re-traces the shot itself:
- start from its own lag-compensated (rewound) shooter eye/muzzle;
- follow the client's aim direction, sanity-checked against view-rotation rate limits;
- take the first collider hit; headshot = that collider is the head.

For melee, the head must be inside the authoritative swing volume.

**Cheap interim step** (optional, R1b or U3): require the claimed point to lie within a few degrees of `intent.Origin + t·intent.Direction`, and require line of sight to the claimed point.

### H2 (Medium, later): melee reach slack is +1.3 m, and the authority has no arc or facing check
`:181` accepts a distance up to `reach + rangeTolerance(0.8)`, measured to a point the client may pull 0.5 m toward itself (`TargetBodyRadius`). In centre-to-centre terms, the Ashen Short Sword (reach 2.3 + hit sphere 0.6 = 2.9 m) is accepted out to about 4.2 m from the attacker's `CombatCenter`. `HitScanner.CollectMeleeHits` would only offer 2.9 m from the eye (`DamageAndScanning.cs:219`). The authority also has no arc check, so a forged melee hit on a target behind the attacker is accepted.

The old authority was unbounded, so this is not a regression. But a 45% reach extension matters in a melee game. For ranged the same slack (1.3 m on 34–46 m) is negligible. Falloff can be shaved by at most 0.5 m, about 0.44 damage on the bow, which is also negligible. The R1a.md wording "a claim cannot shorten the distance by more than the body radius" is accurate. The XML doc on `TargetBodyRadius` ("cannot shorten the distance") overstates it.

**Fix later:**
- use a melee-specific tolerance of about 0.3 m;
- check the authoritative arc against `instigator` facing (players expose `Facing`).

### Other point-1 notes
- **Null/NaN:**
  - NaN or Inf in the hit point, origin or `MaxRange` is rejected through the negated comparisons.
  - A NaN `Charge` gets through `Mathf.Clamp01`, but it produces a NaN amount and `IsValid` rejects it ("resolver produced no damage").
  - `DamageResolver.Resolve` is public. If a caller passes `weapon == null` it throws, but the authority never does that.
- **1.5 m tolerance vs authored bodies:** the elite head's top corner is about 1.49 m from `CombatCenter`, because `CombatCenter` is a fixed +1.15 that does not scale with `bodyScale`. An honest ray clipping the extreme corner of an elite's head is at the limit. A future larger enemy (boss) would have legitimate hits rejected silently. Derive the tolerance from the target's collider bounds, or expose it on `ICombatEntity` (L4).
- **Overlapping head and body colliders:** the body box top (y 1.675) overlaps the head box (y from 1.63). A melee `ClosestPoint(eye)` on the body front face lands 0.07 m outside the head box, just above the 0.05 m epsilon, so there are no spurious headshots today. The margin is thin.
- **`HitFeedback` / `DamageInfo.Point`** is now the clamped point. Head-hit damage numbers and VFX will appear about 0.2 m below the head. This is cosmetic.

## Point 2: behavioural regressions

- **Damage values:** melee is unchanged (no falloff). Ranged falloff is now measured from the shooter's `CombatCenter` to the clamped point, not from muzzle to surface. That is about 0.5 m different, which moved the 25 m bow figure by about 0.45–0.56. This change is intended and documented.
- **Cooldown spent only on accepted attacks:** no exploit.
  - Spamming rejected attacks deals nothing. It only burns the client's own sequence numbers, because the sequence is consumed at `:149` before the cooldown check, as before.
  - Spamming accepted attacks is still gated by `now - last < cooldown * 0.8`.
  - The change also removes an old bug: a range- or LOS-rejected attack used to stamp the cooldown and block the next legitimate swing.
- **Enemy attack / telegraph path:** broken for off-axis hits inside the wedge (M1). On-axis hits work: the claimed `maxRange` (2.6 / 2.9) is within claw reach (2.65 + 0.8) and maul reach (3.2 + 0.8).
- **`Time.time` vs timeScale:** `Now` is `Time.time`. That is the same clock the clients already stamped and `MeleeCombat._readyTime` uses, so pausing (timeScale 0) freezes both consistently. There is no change in behaviour. A real server would use its own tick clock.
- **Ammo (new follow-up, not in R1a's listed scope):** `RangedCombat.Fire` decrements the magazine client-side (`RangedCombat.cs:236`), and the authority never checks ammo for weapon attacks. A forged client fires forever without reloading, limited only by the cooldown. Add this to R1b/U3 together with `Charge`, which is already noted.

## Point 3: test integrity

### Changed assertions and fixtures, file by file

**`EmberdelveRuntimeSmokeTest.cs`**
1. `:302` comment rewritten. No logic change.
2. Combat validation block, `:428-531`:
   - The enemy is frozen (`enabled=false`) and later re-enabled if alive. This is reasonable now that the block waits in real time.
   - `tooFar`: the timestamp became `Time.time`, plus a **new** reason check, `Contains("not on the target")`. **Coverage shift:** this case used to prove melee *range* rejection. It now proves hit-point rejection. The label still says "beyond weapon reach". After R1a nothing tests a melee target genuinely beyond reach (the new gate only does crossbow at 60 m). This is a small gap (L1).
   - `replay`: timestamp neutralised, plus a new `Contains("sequence")` check. Stronger.
   - Cooldown probe: rebuilt properly. One legitimate accepted attack, then an immediate attack stamped +100 s that must be rejected for "cooldown". **Stronger:** the old probe only failed because the rejected `tooFar` had stamped the cooldown, which is exactly the F2 illusion.
   - `AcceptedCount == acceptedBefore` became `acceptedBefore + 1`. Correct, because one real accept now happens in that window.
   - Valid-attack loop: `WaitForWeaponReady` plus `Time.time` replaced `baseTime + 5 + i*1.5`, and `WaitFrames(2)` became `WaitFrames(1)`.
   - **Small weakening (L2):** `accepted` now starts at `firstAccepted ? 1 : 0`, so `Check(accepted > 0)` at `:526` passes even if all 16 loop attacks are rejected. `enemy.IsDead` at `:528` still needs loop hits in practice (70 HP against about 35 per hit), unless the first attack killed a weakened target. Count the loop's acceptances separately and assert those are greater than 0 whenever the loop ran.
3. `GuardReductionFlow`, `:774-829`: the `stamp`, `+2` and `+4` stamps became `WaitForWeaponReady` plus `Time.time`. The enemy is already frozen at `:732`. All checks are unchanged.
4. `DeathFlow`, `:1154-1172`: `baseTime + i*1.2` became a wait per iteration. The enemy AI stays live here, as before. Checks are unchanged. It now takes up to about 24 × 1.6 s of real time, though in practice it stops when the player dies (about 8 hits).
5. `SubmitMeasuredGuardHit`, `:3136-3155`: `Time.time + 60 + i*3` became wait plus `Time.time`. `MeasuredStampSpacing` was removed, which is dead code after the change.
6. `:3539-3546`: comment and log text only.
7. New `WaitForWeaponReady` at `:4903-4915`. It adds a `Check` per call, which makes check totals depend on timing ("death 39/40"). This is a nit: totals can no longer be compared between runs to spot a lost check. Consider not counting it, or logging instead.

**`EmberdelveAttributeActionGate.cs`**
- The gate's monotonic `_clock` is now injected as the authority clock. Before, it was forged into the intent. The rate, range and LOS semantics are identical: the "too fast" probe still rides `_clock + 0.05`.
- The crossbow now reaches the hand through the authored secondary slot, with new swap checks. Before, the crossbow was named while the sword was held, which only worked because of F1. This is legitimate.
- `PlaceTarget` now measures from `CombatCenter` instead of `AimOrigin`, so the asserted distance equals what the authority measures. **No expected value changed:** 34.944, 24, 25.536, 26.448, 43.68, 44.9344, 31.45408, 34.3427, 21.28 and 22.04 are all untouched. The reason given (34.3427 was off by 0.0146 with the eye origin) is consistent with the geometry.
- The out-of-range case (60 m crossbow) is now rejected by the authority's own distance instead of the claim. Same expectation, more meaningful.

**`EmberdelveAttributeSkillGate.cs` (the 38.97658 expectation)**
- The old constant matches a 24.75 m muzzle-to-surface distance. The new expectation is `DamageAtRange(dist(rig.CombatCenter, captured.Point)) × chargeLerp × rig multiplier`.
- **It is partly tautological:** it recomputes the authority's own formula from the authority's own output (`DamageInfo.Point` is the clamped point the authority chose). If the authority measured from the wrong origin, the final `CheckClose` would still agree.
- **What still bites:**
  - `|authDist − 25| < 1`;
  - `|expected − 38.97658| < 1.2` (about 1.36 m of falloff at 0.88 damage/m, about 3%);
  - `expected <` the damage without falloff;
  - the multiplier is independently pinned at 1.3216 (`:2200`).
- So the falloff ramp is still covered, but loosely: about ±1 m instead of exact. **Verdict:** weakened, not hollow, and not shaped to pass a wrong number. **Should fix (S1):** derive the expected point independently. Run a fixture-side raycast from `rig.GetMuzzlePosition()` along `rig.AimDirection`, clamp with `host.CombatCenter + ClampMagnitude(rayHit − centre, TargetBodyRadius)`, and assert both `captured.Point` and the damage against that. Alternatively, place the host so its centre is exactly 25 m from `rig.CombatCenter`. The 10 m shots are unchanged (inside the falloff start).

**`EmberdelveArmorAffixGate.cs`**
- `clock += 5` is now the injected authority clock. The fake `Actor` gains `IActiveWeaponProvider` holding the gate's claw.
- Expected values (15, 15·100/110, 15·0.18·100/110, Holy 15) and all reference checks are unchanged. No weakening.

**`EmberdelveFlatDamageAffixGate.cs`**
- Injected clock.
- The fake target gets a real 0.3 m `SphereCollider` head at the hit point, enabled only for the headshot case, so the 70.1568 headshot expectation now comes from geometry.
- All expected numbers (34.944, 38.976, 70.1568, 48.8992, 34.22944, 35.45192, 44.9344, 18.816, 63.3733632) are unchanged. Distances are exact because the claimed point equals the target centre, so there is no clamp.
- Body hits disable the head, so this gate no longer shows that "a body point with a head present is not a headshot". The new gate covers that (`forgedHead`). No weakening.

### Would the new gate's 48 checks fail on the old authority?
Literally, yes: `SubmitAttack(..., out string)`, `SetClock` and `LastAttackRejectReason` do not exist there, so it would not compile. Semantically, I traced each case against the old logic:
- `forgedWeapon` (rapier), `strongerClaw`, `nullWeapon`: old would accept, or reject with "weapon data missing" for null, which fails the `Contains("not the equipped weapon")` check. **Fail.**
- `farForgedRange` (claim 1000 at 60 m): old would accept, since 60 ≤ 1000.8. **Fail.** `inReachForgedRange`: old accepts. **Fail.**
- `atMuzzle`: old distance 0, accepted with no falloff. **Fail.**
- `forgedHead`: old gives x1.8 and `Headshot=true`. **Fail.**
- `future` (+5000 after +1000): old accepts. **Fail.** `half`: old stamp-based, accepts. **Fail.**
- `noWeapon` (no provider): old accepts. **Fail.**
- `shortClaim` (falloff at 29.5 m): old measured to the claimed point, 28.6 m, so it **fails** the 0.02 tolerance.
- **Not discriminating:**
  - `nan`: old also rejects it, via a NaN amount, and the reason is not pinned.
  - `deadTarget`: reason not pinned.
  - the identity rules: pinned, but unchanged behaviour.
  - R1a.md says every check "pins the rejection reason". That is slightly overstated: NaN and dead target do not.
- **Missing:**
  - a melee target beyond real reach (L1);
  - the off-axis enemy hit (M1);
  - LOS (levelMask is 0 here; AttributeActionGate covers LOS);
  - a large-target or elite tolerance boundary.

### Reason strings
Asserted by `Contains` on free-text log messages ("cooldown", "sequence", "dead", "self", "weapon reaches", ...). This is acceptable for now and better than nothing, but brittle. "dead" matches both "instigator is dead" and "target is dead", and any rewording breaks tests. **Later:** expose a `RejectReason` enum (or const string codes) from the authority, have tests assert the code, and keep the text for logs.

## Point 4: compile / API, performance, dead code

- **Compile:** all 17 logs show PASS with no `error CS` (I checked `Logs/r1a_*`). The changes are additive: `ICombatAuthority` is unchanged, the overload has an `out` parameter, and the interface lives in `Emberdelve.Core` with a fully qualified `WeaponData`. The new serialized field `hitPointTolerance` is missing from the scene YAML and falls back to its 1.5 initializer.
- **`SetClock` is public on a runtime MonoBehaviour (L6):** any runtime script can replace the authority clock. In-process code could already call `ApplyDamage`, so locally this is harmless. But it is a test seam on the "server" API. Make it `internal` with `InternalsVisibleTo` for the editor assembly (or `#if UNITY_EDITOR || DEVELOPMENT_BUILD`) before U3.
- **Allocations:** the accept path does not allocate (structs only, plus one `ClosestPoint` query when a head exists). Reject paths build interpolated strings and `Debug.Log` when `logRejections` is on, same as before. No issue.
- **Dead or near-dead code:**
  - `intent.Timestamp` and `intent.Origin` are now only sanity data (documented).
  - `intent.MaxRange` is used only to reject a forged claim; it never widens anything.
  - The `intent.Direction` fallback (`:198`) only applies in a degenerate case.
  - `MeasuredStampSpacing` was correctly removed.

## Later follow-ups (ranked)

1. **S1** (Medium): make the 25 m archer expectation independent of `DamageInfo.Point` (see point 3).
2. **H1** (open for U3): server re-trace for hit point and headshot. Interim: validate the claimed point against the aim ray and LOS to the point.
3. **H2** (Medium): melee tolerance and authoritative arc/facing check. The current slack is +1.3 m on the sword.
4. **R1b** (blocking for the F1 claim): `SubmitSkill` still trusts everything R1a fixed for weapons.
5. **Ammo and charge** are not authority-tracked for weapon attacks (`RangedCombat.cs:236`).
6. **L1:** add a melee "target beyond real reach" negative test, and re-label the smoke `tooFar` check (`:445-447`).
7. **L2:** count the smoke loop's acceptances separately (`:497`, `:526`).
8. **L3:** the claimed-`MaxRange` rejection couples `EnemyData.attackRange` to weapon reach. An authored `attackRange` above `reach + hitSphere + 0.4` silently rejects every enemy swing. Add a data validation check, or have `EnemyAgent` claim the weapon reach.
9. **L4:** derive `hitPointTolerance` per target from collider bounds. The elite head corner is already at 1.49 m of 1.5.
10. **L5:** U3 needs an equipment-change sequence so that a legitimate shot crossing a weapon swap is not rejected.
11. **L6:** restrict `SetClock`. Use a `RejectReason` enum in tests. Make the `WaitForWeaponReady` check count deterministic.
12. Cosmetic: feedback point is clamped (head-hit numbers draw low). The `TargetBodyRadius` XML doc overstates the guarantee.
