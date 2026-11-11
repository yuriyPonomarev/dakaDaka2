# DakaDaka — current work

Updated 11 November 2026. Rules: ../AGENTS.md. Roadmap: ROADMAP.md.
User: autonomous work, minimal sufficient checks, commit only completed milestones.

## Latest completed: LIGHT-01 torch and source lighting

Pitch Torch is a UID inventory item in both starter bags; equip with inventory right-click, Q swaps sets/stows it. Owner and remote players see its flame and actual light. Removed phantom camera light; hands receive shadows, ambient is darker, point sources use soft shadows and small depth-occluded scattering. E extinguishes/relights authored local lights, including flame/glow; server validates reach/LOS and replicates state to late joiners. Moon remains environment light. Protocol11.

Report: Tasks/LIGHTING_TORCH_20261111.md. Final build: Builds/Playable/20261111-154101-549/EmberdelveNet.exe. Final Hands92/0, Visual207/0/build PASS. Combined focused51 unique tests, harness15/0; network baseline NetInventory148/0, NetDoor37/0, NetFinal172/0, NetCombat312/0. Catalog143/0 after canonical serialization fix. Live host/client/latejoin toggle accepted; final owner/observer equipped-stowed-relit torch visually verified, seed11/protocol11. All review windows closed; main Editor untouched. No repeat of the full suite for cosmetic fixes.

Limits: simple flame meshes/local scattering, not room-scale volumetrics; torch uses a weapon slot and no fuel system. Old unrelated materials, Human import formatting, Logs and Pipeline config changes remain uncommitted.

## Recent accepted baseline

Sword hilt lowered below frame, upright35-degree ready retained, guard unchanged; bow accepted. Finger/palm grips and smoother weapon transitions completed. See Tasks/LOW_SWORD_20261111.md, FINGER_GRIP_20261111.md, WEAPON_TRANSITIONS_20261111.md.

Open moonlit ruins have terraces/stairs, dormant-to-aggro undead and server-owned breakable barrels. Whole CC0 Innerscene/MakeHuman source supplies class bodies and first-person hands. See Tasks/MOONLIT_RUINS_20261109.md and UNIFIED_HUMAN_20261109.md. Current armour/materials remain simple.

## Next actions

1. Follow the next user-selected slice; no background full-suite reruns. Lighting is complete with the limits above.
2. Existing separate visual work: sleeve seams, third-person bow draw state, crossbow model/reload, brief magenta impact material. Do not bundle these into the finished light milestone.
3. Preserve and review inherited material/import/log/Pipeline drift separately; never blanket-add it to commits.
