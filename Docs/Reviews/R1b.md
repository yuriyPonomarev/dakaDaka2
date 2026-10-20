# R1b independent review: the authority owns skill casts, statuses and ammo

Reviewer: independent, clean context, read only. I made no edits other than this file, ran no git writes and did not run Unity.

Scope reviewed:
- `git diff HEAD` (12 files) and the untracked `Assets/Emberdelve/Editor/EmberdelveSkillAuthorityGate.cs`;
- `Docs/Tasks/R1b.md`, `Docs/Reviews/R1a.md`, `AGENTS.md`, and F1 in `Docs/Reviews/CODEBASE_HEALTH_20261020.md`;
- the real callers (`SkillCaster`, the four behaviors, `RangedSkillUtility`, `RangedCombat`, `PlayerMovement.TryStartDash`, `ClassLoadout`, `ManaComponent`, `HitScanner.CollectRadialHits`, `DamageResolver.ResolveSkill`) and the skill assets;
- `Logs/r1b_*` and `Logs/r1a_*`. All `r1b` logs are newer than the final source files. None contains `error CS`, and no run has a FAIL line.

## Verdict: ACCEPT-WITH-FOLLOWUPS (M1 and M2 must land before commit)

The core design holds up. On the skill hit path I found no bypass:
- A hit needs an activation that the authority opened for that caster id.
- The skill, weapon, scale and budget come from that activation.
- Kit, mana, ammo, weapon and cooldown are checked and spent inside `TryBeginSkillCast`.
- `ApplyStatus(spec)` is gone, and the only runtime status writers left are the two calls inside the authority.
- Ranged weapon ammo is spent exactly once per shot.

I found no gameplay regression on the real rig. The fixture edits do not weaken any assertion.

Two things block the commit:
- **M1:** `CancelSkillCast` is a caller-driven refund that no shipped content actually needs.
- **M2:** the smoke fixture "rewrite" never executes, so R1b.md claims a check that never ran.

R1b.md also misstates the cause of the combat 231/234 difference. That is documentation only, and is listed under M2.

---

## Must fix before commit

### M1 (Medium security, cheap fix): `CancelSkillCast` refunds the mana, ammo and cooldown of any cast that has no accepted hit
`LocalCombatAuthority.cs:449-465`: the refund is honoured whenever `Hits == 0 && !Completed`. It has no window and no reason check. The caller decides whether to call it.

**Scenarios** (a forged or modified client calls the public `ICombatAuthority` API):
1. **Free Dash.** `TryBeginSkillCast(dash)` → move locally → `CancelSkillCast` instead of `CompleteSkillCast`.
   - Dash has `HitBudget == 0` (`SkillData.cs:199-202`), so `Hits` is always 0 and the cancel is always honoured.
   - The 24 MP and the 7 s stamp both come back, so Dash has no cost and no cooldown.
   - This is exactly the F1 row that R1b claims to close: "cooldown on its own clock".
2. **Risk-free projectile skills.** Open Multi-shot (3 rounds + 26 MP + 9 s cooldown), submit hits, and cancel if nothing landed. Everything is refunded, so a whiffed volley costs nothing.
3. **Cancel after the window.** It still works up to `window + 5 s` until `PurgeExpiredActivations` (which only runs inside the next `TryBeginSkillCast`) drops the record.

**No legitimate caller needs it.**
- The worker's example, "a dash into a wall", is wrong. `PlayerMovement.TryStartDash` (`Movement/PlayerMovement.cs:207-225`) returns false only when `controller == null || _class == null`, which is a configuration error.
- The smoke run proves it: "a wall-blocked Dash still costs its mana: the cast happened" (`r1b_combat.log`).
- Multi-shot and Piercing Shot always return true (`MultiShotBehavior.cs`, `PiercingShotBehavior.cs`), and so does Battle Cry.
- So the refund path exists only for a misconfigured rig, and it hands a cheater a cooldown and cost bypass.

**Severity today:** Dash movement is still client-side, so a cheater can already move freely. The concrete gain is the projectile refund and a broken invariant. It becomes a real exploit the moment U3 makes movement authoritative. It is cheap to close now, and leaving it open means F1's skill-cooldown row is not closed.

**Fix (pick one; option 1 is the smallest):**
1. Move the dash precondition into `DashBehavior.CanCast` (expose `PlayerMovement.CanDash => controller != null && _class != null`).
   - On a behavior failure, `SkillCaster.TryCastSlot` calls `CompleteSkillCast` (close, no refund) and logs an error.
   - Remove `CancelSkillCast` from `ICombatAuthority`. Keep it `internal` if tests want it.
2. If a refund must stay:
   - make it authority-side only, never on the interface;
   - allow it only when `Now == activation.OpenedAt` (same authority tick);
   - never restore the cooldown stamp for a skill whose effect the authority cannot observe (`HitBudget == 0`);
   - expire activations at the window (purge in `SubmitSkill` and `CompleteSkillCast` too).

**Tests to add or replace:**
- a Dash activation cannot be refunded;
- a Multi-shot activation with zero accepted hits cannot be refunded after the window, or at all under option 1.

Today the gate pins the exploit as intended behaviour: section 9, `EmberdelveSkillAuthorityGate.cs:508-515`, "the cooldown stamp was given back (immediate recast)".

### M2 (Verification honesty): the rewritten smoke Piercing Shot block never runs, so R1b.md claims a check that was not executed
`EmberdelveRuntimeSmokeTest.cs:4242-4264` is guarded by `if (lineA != null && !lineA.IsDead ...)`. But the Piercing Shot cast just before it kills `lineA`. Both runs log "Piercing Shot hit the first body (40 -> 0)".

Evidence:
- None of the three labels in that block appears in `r1a_archer.log` or `r1b_archer.log`: "activation id it never issued", "while its cooldown runs", "refused activation added no accepted hit".
- The archer total is `117` in both R1a and R1b. R1b.md says "+1 check, coverage kept", which would give 118.

The block was already dead before R1b, so the worker did not cause this. But AGENTS.md says "do not claim unexecuted checks passed". The new gate does cover the semantics (forged ids at `:457-461`, cooldown at `:454-455`), so no coverage is actually lost.

**Fix:**
- Point the forged hit at a live target, e.g. `lineC`, which is never hit and stays alive. Hostile line of sight uses `levelMask` only, so the bodies in between do not block it.
- Or drop the `IsDead` guard and use `PickAliveEnemy()`.
- Re-run archer. Expect 120 if the whole block now runs: 3 checks, 2 of which are new.
- Correct R1b.md.

**Also correct in R1b.md: the combat 231 vs 234 explanation is wrong.**
I diffed the normalised `ok` labels of `r1a_combat.log` and `r1b_combat.log`. Exactly two labels differ:
- `"a live enemy is available for the authority validation tests"` (smoke `:412`). It is counted only when the real melee swing happens to kill the first target, i.e. when it lands a headshot, so it is geometry-dependent. 1 → 0.
- `"the authority weapon cooldown for #N cleared in real time"` (the `WaitForWeaponReady` helper). The number of real waits depends on timing. 6 → 4.

So no assertion was lost and nothing is masked. But the cause is not "loot/enemy layout". The totals are still non-deterministic, which is R1a's L6 and still open. Make these two counts deterministic (log them instead of `Check`-ing them, or always count them) so that a check total can be compared between runs again.

---

## Point 1: bypass analysis

| Attempt | Result | Where |
|---|---|---|
| (a) Skill damage without `TryBeginSkillCast` | Closed. `SubmitSkill` needs `_activations[id]` with `CasterId == instigator`. `SubmitAttack` is the R1a weapon path and carries no skill multiplier. | `:496-500` |
| (b) Reuse or guess an activation id | Closed. Ids are a sequential authority counter, so they are guessable, but each is bound to its caster. Unknown, <=0, other-entity and spent ids are all rejected. | `:496-500`, `:513` |
| (b) Exceed `HitBudget` | Closed. `Hits` counts only accepted hits, checked against the asset-derived budget (Cry 4, volley 3, pierce 2, Dash 0). | `:513-516`, `SkillData.cs:199` |
| (b) Hit one target twice | Closed. A per-activation `HashSet`. | `:517`, `:563` |
| (b) Hit after 1.5 s | Closed on the authority clock. Intent timestamps are ignored. A completed activation is rejected. | `:503-507`, `:512` |
| (c) Free casts via Cancel | **Open, see M1.** | `:449-465` |
| (d) Skill not in kit / another class / passive | Closed. Looked up in `ISkillCasterState.ActiveSkills` with `IsActive`. | `:334-348` |
| (d) Cast for another caster | **Unauthenticated.** `casterCombatId` is caller-supplied on `TryBeginSkillCast`, `Complete` and `Cancel`, so anyone in process can open a cast for another id, spend that player's mana and ammo, and stamp their cooldown. This is the same identity gap as `InstigatorCombatId` on `SubmitAttack` (F4). U3 must take the caster id from the connection. The gate's "another entity cannot cancel it" (`:511`) only tests a wrong id, the trivial case. | L2 |
| (e) Arbitrary statuses | Closed on the authority API. Target and self statuses come only from the asset through `ScaleStatus`. The only runtime writers are `LocalCombatAuthority.cs:441` and `:570`. `IStatusTarget.ApplyStatus` is still public on `PlayerRig` (`:511`), `EnemyAgent` (`:124`) and `StatusComponent`. That is the in-process caveat, with no runtime caller. | grep |
| (f) Ammo for weapon attacks | Exactly one spend per shot (see below). | `:217-237`, `RangedCombat.cs:247-250` |
| (g) Dash movement | Client-side. Acceptable until U3 (see below). | — |

**(f) traced through `RangedCombat.Fire`:**
- **Accepted:** the authority calls `IAmmoSource.TryConsumeAmmo(1)` → `RangedCombat.TryConsumeAmmo` → `_magazine--`. `accepted == true`, so `Fire` does not spend. Total 1.
- **Rejected, no target, or NaN charge:** the resolver is invalid, so the authority rejects before consuming. `Fire` spends 1.
- **Empty magazine:**
  - `Update` never draws at 0, but `Fire` is public.
  - With 0 rounds the authority rejects ("no ammunition"), `Fire` clamps at `Max(0, …)`, and no damage is dealt.
  - Before R1b this was a free damaging shot, so this closes R1a's ammo follow-up.
- **`HasAmmo` and `TryConsumeAmmo`** run in the same frame on a single thread, so they cannot race.
- **Multi-shot and Piercing Shot:** the behaviors no longer consume. The authority consumes `AmmoPerCast` once at open: 3 rounds and 1 round, matching the old behavior counts. The smoke checks "spent exactly one arrow (2 -> 1)" and the volley checks pass.
- **Reload:** casts during a reload behave exactly as before, since `FinishReload` computes `need` at the end.
- **Weapon swap:** with a melee weapon in hand, `RangedCombat._weapon` is the melee weapon, so `HasAmmo` is false. The authority only asks for ammo when its own weapon `IsRanged`.
- **Enemies:** not `IAmmoSource`, so not gated (`ai` and `death` pass).

**Residual:** the ammo state the authority reads *is* the client component's `_magazine`, and `RangedCombat.RefundAmmo` (`:183`) is a new public "add rounds without touching the reserve" method. It is used only by the authority and the fixtures. See L3.

**(g) Dash, and what U3 needs:**
- The server owns the dash: it runs the same controller move at its own tick.
- It uses its own CHA scale (today `DashBehavior` reads `context.EffectScale` locally).
- The cast and the movement arrive as one command.
- The client only predicts, and the server reconciles the position.
- The server rejects a displacement over `dashDistance * scale` plus walk speed (plus a tolerance) inside the dash window.
- Cancel/refund decisions come from server-observed outcomes only (M1).

---

## Point 2: behaviour regressions (real gameplay)

No regressions found.

- **Legitimate casts:**
  - Combat smoke: Dash cast, mana 70→46, cooldown 7.0 s, 4.60 m moved, wall-blocked Dash still pays, Battle Cry.
  - Archer smoke: volley of 3 separate targets with one arrow each; Piercing Shot 2 bodies, cap respected, exactly 1 arrow, 58.2 damage as before.
  - The new gate drives the real rig's `TryCastSlot` for Dash and Battle Cry.
  - No `Rejected skill` line appears in the combat, archer or attrskill logs, so no legitimate cast was refused by the authority.
- **Cooldown on refused casts:** a refused `TryBeginSkillCast` stamps nothing and spends nothing (`:367-397`; a mana failure after the ammo check rolls back). `SkillCaster` starts the local cooldown only after success (`SkillCaster.cs:283-307`).
  - The local cooldown (sum of `deltaTime`) and the authority's `Time.time` difference are the same quantity.
  - A 0.05 s tolerance keeps the two clocks in sync.
- **Mana refund on cancel:** works, but see M1.
- **Mana and HUD events:** `ManaComponent.TrySpend` and `Restore` still raise `Changed`. Skill state, toasts and rejection events still go through `SkillCaster`. `RaiseAmmo` fires on the authority spend (`TryConsumeAmmo`) and again in `Fire`, which is harmless.
- **KitEpoch across death, respawn and runs:**
  - Death bumps the epoch, and the local cooldowns are cleared at the same moment.
  - A new run reloads the scene, so a fresh authority starts with empty stamps, and `CombatRegistry` ids keep incrementing, so ids do not collide.
  - `ApplyClass` runs only at run start (`RunDirector.cs:68` → `ClassLoadout.Apply`), never on a gear change, so equipping cannot reset cooldowns.
  - `HandleKitRestored` has no runtime caller yet.
  - Open activations are not closed on death, but a dead caster's hits are rejected at `:483`, and the records die with the purge or the scene. This leaks no state across runs (L5 tidies it).
  - The epoch itself is client-owned (L1).
- **Battle Cry must not empower its own damage:** the old order is preserved. Before: `ApplyStatus(self)` ran after the hit loop inside `Cast`. Now: `CompleteSkillCast` runs after `Cast` returns.
  - The real-rig gate pins it: body damage = 14 × the multiplier measured before the cast, and the buff is present afterwards (`:676-682`).
  - The flat cry damage stays at scale 1. That is unchanged; the old intent had no scale either.
  - Weapon skills now take the scale the authority reads from the rig, the same value `SkillContext` used to snapshot.
- **Intended behaviour changes worth a changelog line:**
  1. Battle Cry can no longer headshot. Before, `CollectRadialHits` (`DamageAndScanning.cs:303-315`) could pick the head collider as the "best" collider, since its distance equals the body's centre distance and the first one wins. The old resolver then applied ×1.8 to a cry essentially at random.
  2. A Battle Cry with zero victims now stamps the authority cooldown. The old authority stamped only on an accepted hit; local behaviour was already stamping.
  3. Projectile-skill falloff is measured from `CombatCenter` to the clamped point, the same change R1a made for weapons.
- **Enemies still attack:** no kit is needed for `SubmitAttack`, and there is no ammo gate (`ai` 20/20, `death` 40/40).

---

## Point 3: test integrity

**Changed fixtures:**
1. `EmberdelveRuntimeSmokeTest.cs:4249-4263` (Piercing Shot forged block):
   - `NextActivationId()` became the literal `987654321`;
   - the old single check was re-labelled;
   - two checks were added (a genuine `TryBeginSkillCast` refused with "cooldown", and the accepted count is unchanged).
   - The logic is sound, but the block is dead (M2). Nothing was weakened.
2. `EmberdelveAttributeActionGate.cs:682` and `:732-736`:
   - `BuildRig` adds a `RangedCombat`, so the rig owns a magazine.
   - `SubmitAttackAtClock` tops it up with `RefundAmmo(3)` when it is empty.
   - All expected numbers and counts are unchanged (231 before and after).
   - This is legitimate. Before, the fixture's ranged shots passed only because ammo was not modelled. The top-up is test-side only, and ammo semantics are covered by the new gate.
   - **Nit:** `RefundAmmo` was chosen because it is public; a test seam would be cleaner (L3).
3. No other gate needed edits, and none was edited. I verified that the diff touches only these two editor files plus the new gate.

**New gate (154 checks):** it would not compile against the old authority (`TryBeginSkillCast`, `CompleteSkillCast` and `CancelSkillCast` do not exist, and `SetClock` was public). Semantically, I traced several checks against the old `SubmitSkill`:
- forged ids `99999999` and `pierceAct+1000`: the old code had no record, so `sameActivation` was false and the cooldown was keyed on the timestamp → **accepted → FAIL**;
- 5th Cry target, 4th arrow, 3rd pierce body: no budget → **accepted → FAIL**;
- forged `EffectScale` 1.4 / 0.0001: the old code used `intent.EffectScale` (clamped 1.4 / 0.6) → **damage differs → FAIL**;
- forged `TargetStatus` Empowered ×50: the old code applied `intent.TargetStatus` → **FAIL**;
- claimed `MaxRange` 1000 and the point at the muzzle: the old code measured Origin→HitPoint against the claim → **accepted → FAIL**;
- forged rapier on a skill: the old code used `intent.Weapon` → **accepted → FAIL**;
- the "no `ApplyStatus` on the API" reflection check → **FAIL**.

So the gate discriminates.

**Weak spots (later, L6):**
- `:712-713` ("`Fire` costs exactly one round") does not assert whether the authority accepted the shot. If the aim ray misses the body, it only proves the miss path, so a double spend on the accepted path would go unseen. The `Fire` call reaches the gate's authority because `GameServices.Register` overwrites the slot (`GameServices.cs:18`). Assert `AcceptedCount` advanced, and add a mirror case that is guaranteed to miss.
- The window boundary is not tight: the gate only uses 2 s and 60 s. Add a hit at 1.4 s (accepted) and one at 1.6 s (rejected).
- KitEpoch is tested only on the fake caster (`timed.Epoch++`). No test shows that the real `SkillCaster.HandleDeath` bumps it.
- Reason strings are pinned with `IndexOf` (OrdinalIgnoreCase): "dead", "cooldown", "closed", "hit budget". This is acceptable but brittle, and "dead" matches both the instigator and the target message. The `RejectReason` enum from R1a L6 is still the right fix.
- `:603` "(bookkeeping)" is a filler check that inflates the count.
- `:559`: the Dash forged-status hit result is not asserted (the next check covers its effect).
- Section 9 encodes M1 as intended. Rewrite it with the M1 fix.

---

## Point 4: API, compile, performance, dead code

- **Compile:**
  - There is no asmdef, so the runtime is `Assembly-CSharp` and the Editor scripts are `Assembly-CSharp-Editor`, and `InternalsVisibleTo("Assembly-CSharp-Editor")` (`LocalCombatAuthority.cs:7`) is correct.
  - The existing `SetClock` callers (CombatAuthority, AttributeAction, ArmorAffix, FlatDamageAffix gates) are all editor code.
  - Nothing else implements `ICombatAuthority`; the bootstrap check is only an `is` test.
  - No leftover references to `NextActivationId`, `ApplyStatus(int,int,spec)` or `BattleCryBehavior.ScaleStatus`.
  - All 19 logs: 0 `error CS`.
  - **Fragility:** adding a member to `ICombatAuthority` breaks any future fake authority. The interface now has 4 skill methods, which argues for M1 option 1 (fewer methods).
- **Allocations:**
  - Per cast: one `SkillActivation` and one `HashSet`; per caster, one stamp dictionary. Acceptable.
  - The per-frame paths (`SkillCaster.Update` → `CanCastSlot` → `BuildContext`) allocate nothing new. They only gained an `authority == null` check.
  - Reject paths format strings and `Debug.Log` (as in R1a, gated by `logRejections`).
  - `PurgeExpiredActivations` allocates a `List` only when something expires.
- **Dead code and leftovers:**
  - `SkillActivation.SelfStatusApplied` (`:73`, `:442`) is written but never read.
  - `SkillIntent.Timestamp`, `TargetStatus` and `EffectScale` are kept only for shape compatibility (documented). `SkillContext.EffectScale` is still used by Dash only.
  - `OpenActivationCount` says "currently open" but also counts completed records until the purge.
  - `CancelSkillCast` neither counts nor logs a rejection.
- **The 231 vs 234 combat variance:** see M2. It is non-deterministic check counting (a headshot-dependent conditional check plus timing-dependent waits). It is not masking a failure, but it makes totals useless for spotting a lost check.

---

## Later follow-ups (ranked)

1. **L1 (Medium, required for U3): `KitEpoch` is client-owned.**
   - `SkillCaster.ApplyClass`, `HandleDeath` and `HandleKitRestored` are public and each one bumps `KitEpoch`.
   - The authority discards every cooldown stamp from an older epoch (`:371`), so the client itself holds a "reset all authority cooldowns" button.
   - R1b.md's "no client reset call" is true only nominally.
   - **Fix:**
     - the authority owns the epoch: clear `_skillStamps[id]` and close that caster's activations when *it* applies a killing blow (`target.IsDead` after `ApplyDamage`, `:242` and `:565`), or on a death or respawn event raised by the server-side health;
     - class application is a run start, which already means a fresh authority.
   - The same applies to `ActiveSkills`, `Mana` and `AbilityEffectScale`: in U3 they must be server state, not reads from client components.
2. **L2 (U3): caller identity.** `TryBeginSkillCast`, `CompleteSkillCast`, `CancelSkillCast`, `SubmitSkill` and `SubmitAttack` all trust a caller-supplied combat id (F4). Activation ids are sequential and global. That is fine while they are bound to the caster, but only if the caster id is authenticated.
3. **L3: lock down `RangedCombat.RefundAmmo`.** Make it `internal`, or reachable only through the explicit `IAmmoSource` implementation. Give the AttributeAction fixture a test seam. Today it is a public "free rounds" method on a client component.
4. **L4: pin the area-skill origin at cast time.** Battle Cry hits are measured from the caster's *current* `CombatCenter` for the whole 1.5 s window (`:539`, `:545`). A caster that keeps moving (with a Dash, or forged movement) can collect its 4 victims along a path. Record `CombatCenter` in the activation at open and measure area reach from it.
5. **L5: tidy activations.**
   - Remove an activation on `CompleteSkillCast`. Keep a small closed-id set if the "closed" reason text matters.
   - Purge in `SubmitSkill` too.
   - Close a caster's activations on death.
   - Drop `SelfStatusApplied`.
   - Rename or fix `OpenActivationCount`.
6. **L6: gate hardening** (point 3): assert acceptance in the `Fire` check, add tight window tests, test `HandleDeath` → epoch on the real rig, add a `RejectReason` enum, and remove the filler check.
7. **L7: deterministic smoke check counts** (R1a L6, re-observed in M2).
8. **Carried over from R1a, still open:**
   - H1 (point-choice headshot; projectile skills inherit it through the shared `TryResolveHit`);
   - H2 (melee slack and no arc check);
   - `Charge` is still a client claim;
   - S1 (the archer 25 m expectation recomputes the authority's own formula);
   - L1 to L5 of R1a.
   - R1b did not touch these, and R1b.md lists most of them honestly.
9. **Changelog note:** Battle Cry can no longer headshot (a random ×1.8 before), and a zero-victim cry now stamps the authority cooldown.

## Summary
- **Verdict:** ACCEPT-WITH-FOLLOWUPS.
- **Sound:** the activation design. Kit, mana, ammo, weapon, cooldown, budget, window, per-target dedupe, caster-derived scale and asset statuses are enforced by the authority. Weapon-attack ammo is spent exactly once per shot. There is no gameplay regression and no weakened assertion, and the new gate discriminates against the old authority.
- **M1:** remove or lock down `CancelSkillCast`. Today it gives a free, cooldown-less Dash and refunds whiffed projectile skills, and no shipped content needs it.
- **M2:** the rewritten smoke Piercing block never runs (archer stays at 117), and the 231/234 explanation is wrong. Fix the block and correct R1b.md.
- **Main later item:** L1, the client-owned `KitEpoch` (a cooldown reset button) and the unauthenticated caster ids, both for U3.
