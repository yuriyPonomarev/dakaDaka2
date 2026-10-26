# Opus completion plan — 23 October 2026

Codex orchestrates, reviews, runs Unity and commits. Luna implements game changes. This execution continues the user request to finish Opus work. Original design and the latest recorded user decisions take precedence over old proposal tables.

## Current authorized scope — 26 October 2026

The user chose **only already-started changes and the nearest Opus refactors**. This continuation closes R6c2 (the applied hotbar/HUD work), the started R6d view/selection extraction rebased to the current UI, the prepared R8a assembly/NUnit slice, and the already-started R8b1 loot-service/UID-rule slice. Each needs independent review, actual Unity checks, and a local commit.

The full pure-data/ItemInstance rewrite, D1/R1 follow-ups, networking, persistence, new content, and release stages below remain backlog outside this run. The large historical queue is retained for later work; its presence does not authorize continuing past this bounded closure.

## Preserved decisions

- Continue the existing Unity 6000.2.5f1 project; preserve namespaces, scenes and assets.
- Keep six attributes and mana for Warrior/Archer. Stamina no longer gates sprint or defense.
- Parties up to three; target 6–9 players. Dedicated server plus two real clients is the first network proof.
- Shields never parry. Weapon parry is a separate later capability.
- Preserve exact item UIDs and rolls, atomic transfers and no free healing on equipment changes.

## Current evidence

HEAD at start: cfba6d92. R0/R1a/R1b, item catalog, versioned container snapshots, seeded loot, D1a swept melee and D1b combat flow are committed. R2/R7 actor interactions, scoped events and local multiple-player sessions were left dirty by Opus. Projectile blade interception was completed in the preceding session but overlaps those dirty files.

Saved all 57 initial modified/untracked paths at C:/Users/yrapa/.pi/agent/projects/unity-emberdelve/checkpoints/pre-opus-completion-20261023.

Prior final projectile evidence: focused 50/0; SweptMelee 638/0; CombatFlow 376/0; AttributeSkill 925/0; CombatAuthority 49/0; SkillAuthority 161/0; Archer smoke 120/0. Real Editor diagnostic advanced 39 frames with guard active and matching visible/authority blade poses. See PROJECTILE_WEAPON_BLOCK.md.

Fresh MultiPlayer baseline: 123 checks / 2 failures, both ranged assertions still using the old immediate-damage path (Logs/opus_finish_20261023_01_MultiPlayer.log). Independent review also identified ID-only interaction actor lookup and missing finite-coordinate rejection. These were corrected; the fresh 38-gate batch confirms MultiPlayer134/0. The five legacy fixture groups were migrated and independently rerun; all38 groups now have passing selected evidence (C0_INTEGRATION.md). Raw evidence: Logs/opus_finish_20261023_04_integration; clean runner/authority rerun: Logs/opus_finish_20261023_05_runner.

## Ordered milestones

| Slice | Work | Acceptance before commit |
|---|---|---|
| C0 DONE | Finish/review R2+R7 and integrate projectile work | All38 gate groups pass; exact evidence and fixture corrections in C0_INTEGRATION.md. Offline scope only. |
| R6a DONE | Stable non-reused runtime inventory stack IDs | StackId56/0 and6 regression groups pass (11,875 checks); gear UIDs and snapshot JSON unchanged. See R6a.md. |
| R6b1 DONE | Shared inventory/loadout revision commit plumbing | Revision35/0 and9 regression groups PASS (12,140 checks); exact-once commits and final-state observers. See R6b1.md. |
| R6b2 DONE | Owner-bound sequenced inventory commands; authority owns consumable effects | Authority103/0 plus9 regressions PASS (2160 checks); identity/replay/stale refusal, exact effects and observer errors. See R6b2.md. |
| R6c1 DONE | StackId selection and authority commands for the inventory screen | Independent review; InventoryUI328 plus Authority103/InventoryFlow415/ResultLoop80 PASS (926 checks / 0). See R6c1.md. |
| R6c2 DONE | Stable hotbar assignments and matching HUD | InventoryUI420/0 (incl. hotbar scenarios), Authority103/0, InventoryFlow415/0, ResultLoop80/0, StackId56/0 (1074 checks / 0). Logs/r6c2_r6d_20261026_121547. See R6c2.md. |
| R10a DONE | Immutable effective-stat capture over existing authoritative inputs | Commit4127e494; EffectiveStats17/0, independent literals/nonempty affixes and immutable old captures. Runtime integration remains R10b. |
| R10b DONE | Runtime effective-stat cache/read integration and source-version invalidation | All20 affected groups pass; EffectiveStats25/0. Scope/evidence in R10b.md; no doubled bonuses/free refill. |
| R10c DONE | Central typed affix-key routing and final consumer audit | All13 selected groups pass (25301 checks); 13 stable destinations and old schema/UI preserved. See R10c.md. |
| R10d DONE | Close audited snapshot consumer bypasses | Independent review and seven fresh groups PASS (11259 checks / 0 failures); snapshot UI/ability/speed/defense/regen and no pool refill. See R10d.md. |
| R6d DONE | Selection/command helper extracted to InventoryScreenModel; screen keeps view facade | Same fresh run as R6c2 incl. RebindingDropsSelectionFromTheOldBag; public API of InventoryScreenController unchanged. See R6d.md. |
| R8a DONE | Rules/Runtime/UI/Review/Editor asmdefs and AttributeRules NUnit tests | EditMode 14/0, R8aSavedSerialization 135/0, 44 regression groups PASS (40327 checks / 0). Logs/r8a_20261026_123856. See R8a.md. |
| R8b1 | Finish started loot-service separation and unchanged pure UID helper | Literal old-runtime parity, readable UID NUnit cases, catalog/loot/restore regressions. |
| R8 remainder BACKLOG | Broader pure Data/ItemInstance/Snapshot/LootRoller/Damage math and remaining NUnit port | Separate future scope; R8a/R8b1 do not claim full R8 completion. |
| D1 follow-ups | Turn-rate cap with matching visuals, enemy sweeps, ranged hit locations; close confirmed combat review findings | Geometry/authority/input tests and live rendered review |
| R1 follow-ups | Kit epoch and sender ownership, authoritative movement ability execution | Forged/replayed/other-player commands refused, normal skills still work |
| U3A | Compatible official networking stack; dedicated process and two clients, walking/ownership/late join, offline retained | Real independent process connection/input/cleanup evidence |
| D2 | Complete simulated projectiles: authored gravity, deterministic stepping and flight-time tuning | Drop/falloff/wall/body/blade/location and tick-size checks |
| D3 | Stable rarity extension, equipment slots, weapon sets, utility belt, item movement penalties and gear score | Migration and atomic inventory/equipment checks, usable UI |
| D4 | Accepted six-attribute role remap via authored curves and derived stats | Literal curve points, gameplay effects, save keys preserved; mana remains |
| U3B | Server combat, skills and state replication | Real PvP/PvE, owner/cadence/stats validation and client visual agreement |
| D5/U4B | Configurable raid timer/portals, corpse and interrupted extraction | Exact-once result, death/disconnect cannot bank, exact item ownership |
| U4A | Authoritative contested containers/inventory | Two clients race for one UID: exactly one owner; range/LOS/replay negatives |
| U4C | Versioned atomic server profile/stash persistence and next loadout | Exact UIDs/rolls survive restart; invalid saves refused atomically |
| D6 | Approved small lobby economy and starter kit | Server validates transfers/prices; gold conservation and zero-gold recovery |
| D7 | Approved kit pools and additional archetypes, split by class/system | Real abilities/perks and resource/status behavior |
| D8 | Seeded module dungeon, doors/traps, PvE additions and floor links | Reachable deterministic maps, valid portals, collision/LOS and fight checks |
| D9 | Party/spectate/revive and six-client scale check | Team rules and interrupted revive over the network |
| U5 | Licensed reusable art, animation, audio and readable dungeon-synth presentation | Provenance, inspected live frames, audible playback and measured performance |
| U6 | Windows release candidate and launch/readme tools | Dedicated plus clients complete raid/death/extraction/stash-restart loop |

Large slices are split again before dispatch. No network, content, persistence or release acceptance is inferred from local harnesses. The October 19 roadmap and October 22 pause paragraphs remain historical, not current task state.

## Service limitation observed

Two read-only audit agents stopped with the service usage-limit error before writing their assigned files. R10a was independently verified and committed as4127e494. Continue useful checks and review with available execution; do not label blocked work complete.
