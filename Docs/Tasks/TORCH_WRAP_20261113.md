# Torch head wrap — 13 November 2026

User identified top-only burning. Correct the flame so the combustible cloth head burns around its circumference; preserve the non-burning handle, light range/intensity and item/combat/ownership.

Checkpoint Logs/harness/before-torch-wrap-20261113.json. New HeadWrap particle layer emits from an open cylindrical surface around the .448–.556m cloth band. Dedicated hidden emission mesh (no mesh renderer) and world-space upward velocity. Emitter follows the actual head transform after hand poses; existing upper tongues/core/sparks/smoke remain. 56 additional particle maximum; single light unchanged. AlignFlame is shared by runtime and preview so both use the tilted head position.

Planner selected all71 from shared/new Editor inputs; narrower relevant proof is Visual/Hands and actual first-person movement plus remote/stow review, fresh build. No changed combat/ownership/network logic, no repeat of their green suites. Main Editor no Play/scene changes; root sole mirror executor.

Visual acceptance pending.


Generation v2 PASS. Three close-up views (front/rear/side at different sampled times) and first-person previews inspected; lateral flames now cover the combustible band without reaching the grip. Evidence review-v2/torch-close-25.png,65.png,110.png. Five particle systems, maximum142 particles total, single unchanged point light. Copied only held prefab and new emission mesh to main; body/ground/icon/catalog from the preceding tasks retained.

Visual219/0 and Hands92/0 PASS in Logs/runs/20261113-212159-014. Main compile completed with no errors; first status request landed during reload (temporary EDITOR_NOT_RUNNING), second completed clean in main-compile-retry.json. No main Editor restart/Play/scene change.

First actual build212159-014 passed gates/build but first-person motion exposed world-space particle/viewmodel separation: HeadWrap fire appeared away from the held head. Initial evidence retained under verified-seed-1344668317; not accepted. HeadWrap simulation changed to Local while retaining velocityOverLifetime in World: particles stay with the head's render transform and rise vertically. Fresh generation/build and same live failing scenario required; unchanged green gates not rerun solely for this particle-space correction.

Final build212610-838 PASS (particle-space-only follow-up). Repeated actual movement and turn on two clients: local wrap fire remains at the head in torch-moving.png, torch-wrapped.png and torch-turned.png; no separated fire seen in these final frames. Observer sees the held fire and its removal in torch-remote-front.png/torch-stowed.png. Ground/icon unchanged. Final evidence final-seed-1344668317. Native ThreadsafeLinearAllocator::Deallocate invalid-pointer error recurred in owner during turn and opened Development Console (visible in torch-turned); review continued. This is the previously deferred engine-memory issue, not fixed or attributed away by this pass. Main compilation clean; own test players/server shut down, main Editor left open. Torch-wrap correction complete, no full-suite or engine-stability claim.

## Brighter/larger follow-up requested by user
Checkpoint Logs/harness/before-torch-brighter-20261113.json. Light intensity1.5→2.7 (+80%), range8→10m, slightly less saturated warm light (1,.66,.34). Upper tongues sizes .055–.095x.17–.29→.075–.125x.24–.38m; rate32→48. Denser/larger cloth flames and core, sparks7→9. Still five systems/one light, maximum182 particles; head attachment/local simulation retained. Shared constants keep authoring/runtime light settings consistent. Generation v1 PASS; first-person preview reviewed. Saved held prefab updated in main; body/ground/catalog untouched. Planner broad71 reviewed: visual gate/build/live light comparison sufficient for changed cosmetic parameters, no repeated combat/ownership suites.

Brighter first pass: build213107-000 PASS and Visual219/0. Actual dark-wall drawn/stowed comparison showed an improvement but insufficient illumination for user's request; raised light once more to4.5 (3x original1.5), range12m (original8). Flame parameters from reviewed first pass retained. Follow-up generation/build and same actual lighting scenario only, no repeated green gates. Initial brighter evidence retained in torch-brighter-20261113/final-seed-1344668317.

Final brighter follow-up build213457-297 PASS; main compile clean. Actual same-position dark-wall comparison dark-wall-stowed.png/dark-wall-lit.png shows added illumination of stone and floor, larger flame remains attached in fire-in-motion.png and disappears with its light in stowed-final.png. Evidence torch-brighter-20261113/brighter-seed-1344668317. No Exception/Invalid memory pointer/Shader error matches in this short final player/server pass; previous native error remains unresolved. Cosmetic changes retained in main. Own test players/server closed; main Editor stays open. User's brighter/larger request complete.
