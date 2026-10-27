# R1c plan: kit epoch and sender ownership, authoritative movement ability execution

Roadmap row: "R1 follow-ups | Kit epoch and sender ownership, authoritative movement ability execution | Forged/replayed/other-player commands refused, normal skills still work".
Inputs: Docs/Tasks/R1a.md, R1b.md ("Open follow-ups" L1, L2, F4, dash, caller-driven CompleteSkillCast), Docs/Reviews/R1b.md, R2R7.md, R6b2.md (owner-bound command pattern), OPUS_COMPLETION_PLAN.md. Baseline = current/ tree with the uncompiled drafts B1-B3. Nothing below was compiled or run (no Unity here).

## Problem in one paragraph
The authority validates commands against entity state it reads from the CLIENT-owned rig (mana, ammo, kit, KitEpoch, aim pose) and trusts the combat id inside the command (`InstigatorCombatId`, `casterCombatId`). Another in-process caller, or a client, can therefore (a) act for someone else's entity, (b) move the authority's idea of mana/ammo/epoch by editing its own components, (c) leave a cast open forever by never calling CompleteSkillCast, (d) dash anywhere because movement is client side.

## Design principle
The authority owns per-entity combat kit state in a record keyed by combat id (kit epoch, mana, ammo, cooldown stamps, active cast), seeded or replicated from the rig only at defined points (class apply, respawn, equipment/loadout revision). Every command carries a sender identity (`CombatSenderId`) that the authority binds to a registered owner; the local process is a trusted host that registers each admitted player (sender id = player id). Forged instigator/caster ids, replays and other-player commands are refused BEFORE any state is read or written. U3A swaps the sender source (connection/session id, the future `IAuthorityConnection`) without changing the contract.

## Slices (each committable alone, each ends with the gate list green)

### R1c1 sender ownership/identity binding (IMPLEMENTED in current/, see Docs/Tasks/R1c1.md)
- Design: `CombatSenderId` (none / trusted host / player), `CombatSenders` owner table (combat id -> sender, bound by `RunDirector.TryJoin`, cleared by `CombatRegistry.Unregister/ResetForRun`), rule `Authorize(sender, id)`: owned id accepts only its owner, unowned id (enemy, fixture rig) accepts only the trusted host, no sender never. Sender-bound overloads on `ICombatAuthority` for SubmitAttack, TryBeginMeleeSwing, LaunchProjectile, TryBeginSkillCast, CompleteSkillCast, SubmitSkill, LaunchSkillProjectile; player components (MeleeCombat, RangedCombat, SkillCaster, BattleCry, RangedSkillUtility) send as `PlayerRig.CombatSender`. Old overloads untouched (local trusted path).
- Files: Runtime/Combat/CombatSenders.cs (new), LocalCombatAuthority.Senders.cs (new), CombatContracts.cs, RunDirector.cs, PlayerRig.cs, MeleeCombat.cs, RangedCombat.cs, SkillCaster.cs, BattleCryBehavior.cs, RangedSkillUtility.cs; gate Editor/EmberdelveSenderOwnershipGate.cs.
- Gates affected: all combat gates compile against unchanged overloads (no edits); behavior of real components changes only by sender; MultiPlayer, SkillAuthority, AttributeSkill, Archer, Combat, SweptMelee, EnemySweep must stay at their counts. New: SenderOwnership.
- Risks: see R1c1.md (legacy sender-less path still open for player ids; interface grew by 7 members; process-wide static table).

### R1c2 authority-owned kit state (L1: KitEpoch, mana, ammo, kit, cooldowns, active cast)
- Design: `CombatKitState` record per combat id inside the authority: `Epoch`, `Mana` (current/max/regen), `Ammo` (magazine/reserve), `ActiveSkills` snapshot, cooldown stamps (already there), open activation per caster. Seeded by an explicit host call `authority.SeedKit(combatId, KitSeed)` from the places that already bump `SkillCaster.KitEpoch`: ApplyClass, HandleDeath/respawn, HandleKitRestored, plus the equipment/loadout revision (R6b1 shared revision) for weapon/ammo caps. TryBeginSkillCast and SubmitAttack read and spend ONLY this record; the rig mirrors it (events back to ManaComponent/RangedCombat for HUD) instead of the authority reading the rig. `ISkillCasterState`/`IAmmoSource` shrink to seed providers. Seed calls take the sender (host only; a player sender cannot reseed).
- Files: new CombatKitState.cs + LocalCombatAuthority.Kit.cs; SkillCaster, ManaComponent, RangedCombat (mirror, reload/pickup report through the authority), PlayerRig, ClassLoadout, RunDirector (seed at join/respawn), interface members; gates SkillAuthority (fake casters seed instead of exposing state), AttributeSkill, Archer, Death, Extraction/ResultLoop (respawn paths).
- New checks: editing the rig's ManaComponent/RangedCombat/ActiveSkills directly does not change what the authority accepts; stale epoch cooldown reset only through a seed; reseed by a non-host sender refused; HUD still shows the authority's numbers.
- Risks: two sources of truth during the transition (HUD, pickups, regen, reload); mana regen and reload timers need an owner; ammo reserve pickups and inventory use (R6b2 already spends ammo through the inventory authority) must route through the same record. Highest-risk slice.

### R1c3 authoritative movement ability and cast lifecycle, close the legacy path
- Design: (1) Dash: authority validates and applies the displacement (`MovementAbilityRequest` with start pose claim, direction; authority recomputes the path with its own capsule/wall cast and publishes the resulting position; the client reconciles). (2) `CompleteSkillCast` becomes authority-driven: the activation closes and applies its self status at window end or on the last accepted hit, so skipping the call costs nothing and gains nothing. (3) Close the sender-less overloads for player-bound ids (flag `RequireSenderForPlayerCommands`, default on), migrate fixtures to a sender helper, keep trusted-host overloads for enemies. (4) Optional: per-sender replay window instead of per-entity sequence.
- Gates affected: every gate that calls the sender-less overloads on rigs (about 15 Editor gates) moves to a helper; SkillAuthority, AttributeSkill, Death, Combat, Archer re-run; new MovementAbilityGate; SenderOwnership gains "legacy closed" checks.
- Risks: dash feel (latency/rollback) and physics parity between client CharacterController and authority; moving the self-status timing changes Battle Cry feel; wide gate churn.

## USER DECISIONS NEEDED
1. Legacy sender-less overloads: keep as the trusted local path until R1c3 (what R1c1 does) or force the migration of the ~15 gates inside R1c1/R1c2? Default taken: keep, close in R1c3.
2. Dash (R1c3): authority executes movement (server-side capsule sim, biggest change) or authority validates a client-reported dash result against distance/wall rules (cheaper, still bounds a cheater)? Needs a product call on acceptable input latency before U3.
3. R1c2: who owns mana regen and reload timers while there is still a local process (authority tick vs rig tick mirrored)? Affects HUD code and the Death/Extraction gates.
4. Trusted host semantics: R1c1 says the host acts only for unowned ids and never for players. If the future server needs host-driven player actions (cutscenes, forced moves), that needs an explicit second sender kind. OK as is?
5. Naming: keep `CombatSenderId`/`CombatSenders` or introduce the `IAuthorityConnection` abstraction now so U3A has a hook? Default taken: plain struct now, connection abstraction in U3A.
6. Slice 1 and 2 could be merged if you prefer fewer commits; the plan keeps them apart because slice 2 is the risky one.

## Verification plan per slice
Each slice: new gate PASS, then the regression set used by R1b (combat, archer, ai, death, extraction, resultloop, lobby, items, inventoryui, ChestInstance/Partial, AttributeSkill/Action/Vitals, ArmorAffix, FlatDamageAffix, CombatAuthority, SkillAuthority) plus MultiPlayer, SweptMelee, EnemySweep, ProjectileBlock, RangedLocation, TurnCeiling. Totals recorded in each Docs/Tasks/R1cN.md.
