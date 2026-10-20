# R1a: weapon attacks no longer trust client data (F1 weapon half, F2)

Scope: `LocalCombatAuthority.SubmitAttack` only. Skills (`SubmitSkill`, `ApplyStatus`, `SkillIntent`, `EffectScale`, ActivationId) are untouched and remain R1b.

## Design
`SubmitAttack(intent, hit)` (and new `SubmitAttack(intent, hit, out string reason)`; `LastAttackRejectReason` also exposed) now treats the intent as a request:

1. **Weapon**: new optional interface `IActiveWeaponProvider { WeaponData ActiveWeapon }` (GameContracts.cs). `PlayerRig` returns `Loadout.ActiveWeapon`; `EnemyAgent` returns `Data.attackWeapon`. The authority resolves the weapon from the instigator entity and rejects the intent when `intent.WeaponId`/`intent.Weapon` differ. An instigator that does not implement the interface cannot attack at all (reject by default). Damage, reach and cooldown use the resolved weapon.
2. **Range / hit point**: reach is derived from the weapon (`rangedRange` for ranged, `reach + hitSphereRadius` for melee). A claimed `MaxRange` above reach + `rangeTolerance` is rejected (it is never used to widen anything). Distance is measured from the instigator's own `CombatCenter` (the intent origin is only sanity checked, existing 4 m rule) to the target. The claimed hit point must be within `hitPointTolerance` (`DefaultHitPointTolerance = 1.5 m`, serialized) of the target's own `CombatCenter` or the attack is rejected ("not on the target"); an accepted point is pulled into a `TargetBodyRadius` (0.5 m) sphere around the target centre, so a claim cannot shorten the distance by more than the body radius and falloff is always applied. NaN points are rejected. Line of sight is checked between the two entity centres. Damage direction/source position are derived from entity positions, not from the intent direction.
3. **Headshot**: decided by the authority: true only if the target's `HeadCollider` is enabled and the claimed point lies on it (`HeadSurfaceEpsilon = 0.05 m`). `HitCandidate.Headshot` is ignored. (Note: a client can still choose to aim at the head; aim itself is client input.)
4. **Clock**: cooldown uses `LocalCombatAuthority.Now` (`Time.time`, or an injected `SetClock(Func<float>)`), never `intent.Timestamp` (field kept, ignored). Only an ACCEPTED attack now spends the cooldown (before, even a range-rejected attack stamped it). Sequence replay protection unchanged (same position in the rule order: after identity/weapon/hostility, before cooldown/geometry).
5. `DamageResolver.Resolve` now passes the resolved weapon (not `intent.Weapon`) to `Finalize`. The intents/structs are unchanged plain data; the authority builds its own authoritative intent/hit candidate for the resolver. `InstigatorKind` in the damage is the entity's own kind.

## Files changed
- `Runtime/Core/GameContracts.cs` (IActiveWeaponProvider)
- `Runtime/Combat/LocalCombatAuthority.cs`, `Runtime/Combat/DamageAndScanning.cs`
- `Runtime/Player/PlayerRig.cs`, `Runtime/AI/EnemyAgent.cs` (implement the interface)
- New gate `Editor/EmberdelveCombatAuthorityGate.cs` (`EmberdelveCombatAuthorityGate.Run`)
- Fixtures: `Editor/EmberdelveRuntimeSmokeTest.cs`, `EmberdelveAttributeActionGate.cs`, `EmberdelveAttributeSkillGate.cs`, `EmberdelveArmorAffixGate.cs`, `EmberdelveFlatDamageAffixGate.cs`
- `MeleeCombat`, `RangedCombat`, `EnemyAgent.SubmitAttack` callers unchanged (they already sent Time.time and honest values).

## New negative tests (EmberdelveCombatAuthorityGate, 48 checks, each pins the rejection reason)
Forged weapon (rapier, enemy claw, null weapon; sword while crossbow equipped; crossbow accepted after a real loadout swap); forged MaxRange 1000 (far and in-reach target); target beyond real reach; hit point at the attacker's muzzle; NaN hit point; hit point shortened inside the tolerance (measured at the body surface, no falloff skip, expected number computed from `DamageAtRange(29.5)`); forged headshot flag on a body hit (body damage, `Headshot=false`); real head hit with claim=false (x1.8); future / past timestamps do not bypass or extend cooldown, half cooldown on the authority clock still rejected, legitimate attack after the cooldown with a stale stamp accepted; replayed and older sequence; unknown instigator/target id; friendly fire; self hit; dead instigator; dead target; entity without weapon provider; injectable clock.

## Rewritten fixtures (F2) and why
- `EmberdelveRuntimeSmokeTest.cs` combat validation block (was ~432-496): the 16 "valid" attacks stamped `baseTime + 5 + i*1.5` two frames apart are now real attacks: the target is frozen, each waits `WaitForWeaponReady` (real time until `WeaponCooldownRemaining <= 0` on the authority clock) and carries `Time.time`. `spam` previously only failed because the earlier rejected `tooFar` attack stamped the cooldown; it now follows a legitimate accepted attack and carries a far-future timestamp, proving the stamp is ignored; reasons (`not on the target`, `sequence`, `cooldown`) are asserted. Accepted count assertion changed from `== before` to `== before + 1` because one legitimate attack now happens in that window. All original assertions kept.
- `GuardReductionFlow` (was `:727` `Time.time + 30f`): three submissions now wait for the enemy claw cooldown instead of stamps +0/+2/+4. All checks unchanged.
- `DeathFlow`: 24 attacks stamped `baseTime + i*1.2` now wait for the claw's real cooldown per iteration. Checks unchanged (a few extra "cooldown cleared" checks).
- `SubmitMeasuredGuardHit` (was `:3026` `Time.time + 60f + ...`): waits for the claw cooldown, `Time.time` stamp; `MeasuredStampSpacing` removed.
- Stale comments about "synthetic future stamps" (`:302`, `:3501`, `:3410-3419` area) updated; the passive-swing wait remains (it is now simply a real cooldown wait).
- `EmberdelveAttributeActionGate`: its own monotonic clock was a forged intent timestamp; it is now injected as the authority clock (`SetClock(() => _clock)`), so rate/range/LOS tests are unchanged. Weapons named in intents that the rig did not hold (warrior crossbow) are now put in hand through the authored secondary slot (`Loadout.SwitchToSlot(1)`, back to 0 afterwards). `PlaceTarget` measures from `rig.CombatCenter` (what the authority measures) instead of `AimOrigin`: the bow-at-30m expectation 34.3427 was off by 0.0146 with the eye origin. Expected values untouched.
- `EmberdelveAttributeSkillGate` archer bow shot at 25 m (real `RangedCombat.Fire`): the hand constant 38.97658 assumed a muzzle-to-centre distance of exactly 25 m. The authority now measures from the shooter's CombatCenter to the accepted point (`DamageInfo.Point`), ~0.5 m different, which changes falloff by ~0.56. The fixture now derives the expectation from that authoritative distance with the weapon's own `DamageAtRange`, charge lerp and multiplier, and still asserts it is within 1 m and 1.2 damage of the nominal figure and below the un-falloffed damage (+2 checks, 885 -> 889). The 10 m shots are unchanged (inside falloff start).
- `EmberdelveArmorAffixGate` / `EmberdelveFlatDamageAffixGate`: forged `clock+=5` stamps replaced by an injected authority clock (`SetClock(()=>clock)`); the fake attacker implements `IActiveWeaponProvider`; the flat gate's fake target now has a real head `SphereCollider` (enabled only for the headshot hit) because headshot is geometry now. Expected numbers untouched.

## Commands (sequential, one Unity at a time; `bash /tmp/r1a.sh name:Class.Method ...`)
`Unity.exe -batchmode -nographics -projectPath <proj> -logFile Logs/r1a_<name>.log -executeMethod Emberdelve.EditorTools.<Class>.<Method>`; exit codes in `Logs/r1a_exitcodes.txt`. Final-revision results below (0 `error CS` in every log).

| run | final log line |
|---|---|
| combat (RunCombatFlow) | `[Smoke:combat] PASS checks=234 failures=0` (+10: off-axis windup flow) |
| archer | `PASS checks=117 failures=0` |
| ai | `PASS checks=20 failures=0` |
| death | `PASS checks=40 failures=0` |
| extraction | `PASS checks=39 failures=0` |
| resultloop | `PASS checks=85 failures=0` |
| lobby | `PASS checks=19 failures=0` |
| items | `PASS checks=407 failures=0` |
| inventoryui | `PASS checks=158 failures=0` |
| ChestInstance | `PASS checks=21 failures=0` |
| ChestPartial | `PASS checks=17 failures=0` |
| AttributeSkillGate | `PASS checks=891 failures=0` |
| AttributeAction | `PASS checks=231 failures=0` |
| AttributeVitals | `PASS checks=200 failures=0` |
| ArmorAffix | `PASS checks=44 failures=0` |
| FlatDamageAffix | `PASS checks=42 failures=0` |
| EmberdelveCombatAuthorityGate | `[CombatAuthorityGate] PASS checks=49 failures=0` |

All 17 logs (`Logs/r1a_<name>.log`) come from one sequential batch on the FINAL tree after the review follow-ups; every exit code 0, 0 `error CS`, no FAIL lines. Earlier-revision logs are in `Logs/r1a_prev/`. Not run: a reverted-code run proving the new tests fail on old code (reasoned only).

## Review follow-ups (Docs/Reviews/R1a.md)
- **M1** `EnemyAgent.SubmitAttack` now submits `target.CombatCenter` as the hit point (wedge/range gating and committed-direction logic unchanged, decide only WHETHER an intent is sent; `LastAttackWhiffed` is now reset on a submitted attack). New `OffAxisWindupFlow` in the combat smoke (real enemy brain windup, player strafes after the direction is committed): +/-45 deg inside the 120 deg wedge lands 15 damage with exactly one accepted and zero rejected attacks; 100 deg outside the wedge deals no damage, no intent is submitted and the enemy records a whiff.
- **M2** every regression re-run on the final tree (above).
- **L1** new gate check: sword vs honest target 8 m away rejected with "weapon reaches". Smoke `tooFar` label changed to say it tests the hit-point rule.
- **S1** the 25 m archer expectation now comes from a fixture-side `Physics.Raycast` from the muzzle along the aim against the host, clamped with `LocalCombatAuthority.TargetBodyRadius`; the test asserts the accepted `DamageInfo.Point` equals that point (<0.05 m), derives expected damage from it, and keeps the within-1 m / 1.2-damage bounds against the nominal figure.
- NaN and dead-target gate checks now assert the rejection reason.

## Open follow-ups (not done here)
- H1: headshot is still free for a cheating client (it can aim at the head collider) until the U3 server re-trace.
- H2/melee: reach slack is about 4.2 m (reach + hitSphereRadius + tolerance 0.8 + body-radius allowance) and there is no arc check for melee.
- Ammo is not validated by the authority.
- Ranged charge is client-claimed.
- `SubmitSkill` still trusts the client (R1b).
- `LocalCombatAuthority.SetClock` is public; restrict it (editor/test only) before U3.

## Unresolved / not in scope
- `Charge` (ranged draw) is still a client claim clamped to 0..1; a client can always claim full charge. Needs a server-side draw timer (R1b/U3).
- `intent.Origin` / `Direction` and `Timestamp` remain in the structs (ignored or only sanity checked) so the data shape is unchanged; they can be dropped at serialization time.
- Aim itself (which point/ray the client chose, including aiming at a head) is client input by nature; the authority only validates it against geometry. No lag compensation.
- Skills still trust Weapon, MaxRange, HitPoint, Headshot, Timestamp, ActivationId, EffectScale and self-status (R1b). `SubmitSkill` still uses `intent.Timestamp` and the claimed hit headshot flag.
- `LocalPlayer` events (`RaiseDamageDealt`) and single static registry assumptions (F4) untouched.
- Nothing committed.
