# R1b: skills, statuses and ammo no longer trust client data (F1 skill half)

Scope: `LocalCombatAuthority` skill path (`SubmitSkill`, new `TryBeginSkillCast` / `CompleteSkillCast`, status application), ammo for weapon attacks and skills, `SetClock` visibility. Builds on R1a (`SubmitAttack`), whose geometry rules are now one shared helper used by both paths.

## Design
A skill cast is now two steps, both owned by the authority:

1. **Open** `ICombatAuthority.TryBeginSkillCast(casterId, skillId, out activationId, out reason)`. The caller passes a skill id and nothing else. The authority:
   - resolves the caster from `CombatRegistry`, rejects dead casters;
   - resolves the skill from the caster's own kit through the new `ISkillCasterState.ActiveSkills` (active skills of the equipped class; unknown / other-class / passive ids are rejected: "not in the caster's kit");
   - checks the weapon requirement against `IActiveWeaponProvider.ActiveWeapon` (`requiresRangedWeapon`, `requiresMeleeWeapon`, and implicitly: projectile skills need a ranged weapon, weapon-scaled skills need a weapon);
   - checks the cooldown on `LocalCombatAuthority.Now` (per caster + skill stamp; a stamp from an older `KitEpoch` is ignored, see below);
   - validates AND spends mana from the caster's `ManaComponent` (`ISkillCasterState.Mana`) and ammo through the new `IAmmoSource` (rounds = `SkillData.AmmoPerCast`);
   - reads the effect scale from `ISkillCasterState.AbilityEffectScale` (the rig's own CHA value, clamped 0.6..1.4) once and keeps it in the activation;
   - allocates the activation id (authority counter) and records it: caster, skill, weapon, scale, open time, hit budget.
   A refusal spends nothing and stamps nothing.
2. **Hits** `SubmitSkill(intent, hit)` must carry an open activation id. Rejected: unknown id / id of another entity / skill claim different from the activation's / id past its **hit budget** (`SkillData.HitBudget`: Battle Cry = `maxTargets` 4, Multi-shot = 3 arrows x 1, Piercing Shot = 1 x 2, Dash = 0) / same target twice in one activation / outside the **window** (`skillActivationWindow`, 1.5 s on the authority clock) / activation already completed. Weapon claim must equal the activation's weapon AND the weapon currently held (strict, like R1a). Timestamp, EffectScale and TargetStatus in the intent are **ignored**.
3. **Complete** `CompleteSkillCast` (called by `SkillCaster` after the effect ran): closes the activation and applies the skill's own **self status** (asset spec scaled by the caster's own scale) after the hits, preserving the old order (a cry does not empower its own damage). Skipping the call only costs the caster its buff.
4. **No cancel / no refund (review M1).** An earlier revision had `CancelSkillCast`; it let any caller get mana, ammo and cooldown back for a cast with no accepted hit (free Dash, free missed volley) and no shipped content needed it, so it is removed from `ICombatAuthority` and the authority entirely (and `IAmmoSource.RefundAmmo` with it). Feasibility is checked before the cast opens: `DashBehavior.CanCast` now requires `PlayerMovement.CanDash` (controller + class). A wall-blocked dash is NOT a failed cast: it still costs mana and cooldown (the smoke already asserts this). If a behavior nevertheless fails after the cast opened, `SkillCaster` closes it via `CompleteSkillCast`, logs an error, starts the local cooldown and keeps the cost.

Shared with R1a: `TryResolveHit` (claimed range vs authoritative reach, origin attached, hit point must be on the target and is pulled into the 0.5 m body sphere, distance from the instigator's `CombatCenter`, LOS between centres, headshot from the target's head collider geometry). Skill specifics: reach = `skill.radius` (area) or `weapon.rangedRange` (projectile); **area skills never headshot**; the resolved intent uses the instigator's `CombatCenter` as origin (falloff measured as in R1a).

Statuses: `ICombatAuthority.ApplyStatus(target, source, spec)` is **removed** (a caller-chosen spec was the hole). Target statuses come from `SkillData.TargetStatusSpec`, self statuses from `SkillData.SelfStatusSpec`, both scaled by the caster's own scale in `LocalCombatAuthority.ScaleStatus` (moved out of `BattleCryBehavior`; now applies to all skills' target statuses; only Battle Cry has any).
Damage scale: flat-damage skills keep scale 1 (CHA for the cry lives in its statuses, as before); weapon-based skills use the activation's scale once.

Ammo for weapon attacks: `SubmitAttack` refuses a ranged weapon attack from an `IAmmoSource` with an empty magazine ("no ammunition in the magazine") and the authority spends the round on an accepted attack. `RangedCombat.Fire` now spends locally only when the authority did NOT accept the shot (miss / no target / rejected), so exactly one spend per shot. Instigators that are not ammo sources (enemies) are not ammo gated. **Ranged draw `Charge` is still a client claim clamped 0..1** (noted in code; needs a server draw timer, U3).

`SkillCaster`: `TryCastSlot` = local prediction (`CanCastSlot`) -> `authority.TryBeginSkillCast` -> behavior -> `CompleteSkillCast` . It no longer spends mana, ammo or allocates activation ids; `CanCastSlot` refuses when there is no authority. `KitEpoch` (bumped on `ApplyClass`, `HandleDeath`, `HandleKitRestored`) lets the authority forget stale cooldown stamps when the caster's own cooldowns are cleared, with no client "reset" call. Dash scale remains local movement (movement is client side until U3).

`SetClock` is now `internal`; `[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]` at the top of `LocalCombatAuthority.cs`. No asmdef split exists, so other classes in Assembly-CSharp could still call it technically; nothing does (grep). A real guard belongs with the U3 server split.

`CombatRegistry.NextActivationId` removed (clients cannot mint ids).

## Files changed
- `Runtime/Combat/LocalCombatAuthority.cs` (rewritten skill path, shared `TryResolveHit`, ammo, activations, `ScaleStatus`, internal `SetClock`)
- `Runtime/Combat/CombatContracts.cs` (`ICombatAuthority` new methods, `ApplyStatus` removed; `ISkillCasterState`, `IAmmoSource`; `NextActivationId` removed; SkillIntent docs)
- `Runtime/Data/SkillData.cs` (`IsProjectileSkill`, `ProjectilesPerCast`, `AmmoPerCast`, `HitBudget`)
- `Runtime/Player/PlayerRig.cs` (implements `ISkillCasterState`, `IAmmoSource`, public `AbilityEffectScale`)
- `Runtime/Combat/RangedCombat.cs` (`RefundAmmo` (internal, test fixtures only), ammo spend only for unaccepted shots)
- `Runtime/Skills/SkillCaster.cs`, `Behaviors/BattleCryBehavior.cs`, `MultiShotBehavior.cs`, `PiercingShotBehavior.cs`, `RangedSkillUtility.cs`
- New gate `Editor/EmberdelveSkillAuthorityGate.cs` (`EmberdelveSkillAuthorityGate.Run`)
- Fixture: `Editor/EmberdelveRuntimeSmokeTest.cs` (see below)

## New gate: `EmberdelveSkillAuthorityGate` (155 checks, reasons pinned)
Fake casters implement the authority's interfaces (kit, mana, ammo, scale, epoch owned by the "server side"); plus the real rig end to end. Covers: foreign / unknown / passive / empty / null skill id and no-kit caster (nothing spent, no activation opened); insufficient mana (state unchanged, no cooldown stamped, next cast accepted); wrong weapon (rapier, unarmed); no ammo (0, 2 of 3, exactly 3 spends 3 once); dead caster (open and hit); cooldown vs forged future/past timestamps (authority clock decides; window not extended); activation reuse (spent id on same and fresh body, forged ids 0/-3/huge, id of another entity, id after the next cast opened, window expiry, wrong skill claim, null skill, same body twice); budgets (Cry 4+1 rejected, volley 3+1 rejected, pierce 2+1 rejected, Dash 0); kit-epoch reset; no refund exists (no Cancel on the API; a missed volley and a dash keep their mana, ammo and cooldown; another entity cannot complete the activation); forged EffectScale 1.4/~0 vs honest equal damage, and the caster's own 1.3 applied (ratio 1.3); forged TargetStatus ignored, self status only from the asset at the caster's scale and only at completion (no stacking, closed afterwards), hit aimed at self rejected, forged statuses on a Dash apply nothing, no `ApplyStatus` on the API; forged hit point (at muzzle, NaN), range 1000, target beyond reach, forged / null weapon, headshot flag on body hit, real head hit x1.8, point claimed in front pulled into the body sphere, area burst never headshots, cry far outside radius; positives for all four real skills (Dash, Battle Cry 4 targets flat 14 + slow, Multi-shot 3 arrows, Piercing Shot 2 bodies with expected damage computed from the weapon's falloff); real rig: `SkillCaster.TryCastSlot` Dash and Battle Cry on a real collider body (mana spent exactly once by the authority, damage = 14 x the rig's own outgoing multiplier, buff applied after), authority independently refuses a second Dash; real archer: accepted bow attack spends exactly 1 round, empty magazine rejects with no damage and no spend, `Fire` on a real body is accepted by the authority and costs exactly 1 round.

## Rewritten fixtures (never weakened)
- `EmberdelveRuntimeSmokeTest.cs` Piercing Shot block: the "forged" intent minted an activation id with `CombatRegistry.NextActivationId()` and expected the cooldown to refuse it. Ids are now allocated by the authority, so the claim is asserted as two checks: an id the authority never issued carries no hit, and a genuine `TryBeginSkillCast` for the same skill during the cooldown is refused with a "cooldown" reason. The block was dead code before R1b: it was guarded by `!lineA.IsDead` and the Piercing Shot just before it kills `lineA`. It now targets `lineC` (never hit, so alive) and really runs: archer checks 117 -> 120.
- `EmberdelveAttributeActionGate.cs`: its fixture rigs were built without a `RangedCombat` component, so they owned no ammunition and their crossbow/bow attacks (legal before) are now refused ("no ammunition"). `BuildRig` now adds `RangedCombat` (a real magazine through the real loadout swap) and `SubmitAttackAtClock` tops the magazine up with `RangedCombat.RefundAmmo` when empty. These tests are about damage/range/LOS; all expected numbers and counts are unchanged (231 checks as before).
- The other gates (AttributeSkill, ArmorAffix, FlatDamageAffix, CombatAuthorityGate, smoke combat/archer/death) needed no edit: they drive real `SkillCaster` casts or only `DamageResolver`, and pass against the new authority. The combat total differed (231 vs 234 in R1a) for a different reason than first written: one check ("a live enemy is available...") counted only when the real swing kills its target, and the weapon-cooldown wait check count varies with timing. Both now fail loudly but no longer add to the total when they pass, so totals are deterministic.

## Commands
`Unity.exe -batchmode -nographics -projectPath <proj> -logFile Logs/r1b_<name>.log -executeMethod Emberdelve.EditorTools.<Class>.<Method>`, one Unity at a time via a scratch script; exit codes in `Logs/r1b_exitcodes.txt` (final batch, all exit 0).

## Results (final tree; all exit 0, 0 `error CS`, 0 FAIL lines)
| run | final line |
|---|---|
| combat | `PASS checks=227 failures=0` |
| archer | `PASS checks=120 failures=0` (117 -> 120: the dead Piercing block now runs) |
| ai | `PASS checks=20 failures=0` |
| death | `PASS checks=32 failures=0` |
| extraction | `PASS checks=39 failures=0` |
| resultloop | `PASS checks=77 failures=0` |
| lobby | `PASS checks=19 failures=0` |
| items | `PASS checks=407 failures=0` |
| inventoryui | `PASS checks=158 failures=0` |
| ChestInstance / ChestPartial | `21` / `17`, failures=0 |
| AttributeSkill | `PASS checks=891 failures=0` (authorityAccepted=55) |
| AttributeAction | `PASS checks=231 failures=0`  |
| AttributeVitals | `PASS checks=200 failures=0` |
| ArmorAffix / FlatDamageAffix | `44` / `42`, failures=0 |
| CombatAuthorityGate (R1a) | `PASS checks=49 failures=0` |
| SkillAuthorityGate (new) | `PASS checks=155 failures=0` |
All 18 logs come from ONE sequential batch on the final tree (previous revision logs moved to `Logs/r1b_prev/`). Totals changed vs R1a only because the two timing/geometry-dependent checks no longer count when they pass (combat 234 -> 227, death 40 -> 32, resultloop 85 -> 77: each lost the per-wait cooldown checks) and archer gained the 3 now-running Piercing checks. Review follow-ups done: M1, M2, L3 (`RefundAmmo` internal), L6 (Fire check asserts authority acceptance, filler check removed).

## Open follow-ups
- Not executed: a reverted-code run proving the new gate fails on the old authority (reasoned only).
- `SetClock` is internal but there is no asmdef: other Assembly-CSharp classes could still call it. Needs the U3 split or a grep gate.
- Ranged draw `Charge` still a client claim (0..1); headshot still free for a client that aims at the head collider (H1 from R1a); melee has no arc check.
- (review L1, needed before U3) `KitEpoch`/mana/ammo/kit are read from the client-owned rig components; the authority must own that state (or receive it replicated) before a real server exists. (L2) `casterCombatId` on `TryBeginSkillCast`/`CompleteSkillCast` is caller-supplied and unauthenticated, the same identity gap as `InstigatorCombatId` on `SubmitAttack` (F4): another in-process caller could open a cast for someone else; U3 must take the id from the connection. (L4, L5) other reviewer low findings not taken here, see Docs/Reviews/R1b.md. Dash server simulation is not done (movement is client side). `CompleteSkillCast` is caller-driven: skipping it only forfeits the self buff and leaves the activation open until its window ends.
- Dash movement and its CHA scaling remain client side (movement is not authoritative yet); the authority only gates the cast (mana, cooldown).
- Enemies are not ammo gated and have no skill kit.
- `PlayerRig.IAmmoSource` treats a rig with no `RangedCombat` as having no ammo (ranged weapon attacks refused).
- `SkillIntent` still carries Weapon/MaxRange/Timestamp/EffectScale/TargetStatus for shape compatibility (ignored or bounded); drop at serialization time.
- Multi-shot cannot hit one body with two arrows of the same volley (per-target-once per activation), same as the client's existing dedupe.
