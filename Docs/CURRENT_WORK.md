# DakaDaka — current work

Updated 11 November 2026. Rules: ../AGENTS.md. Roadmap: ROADMAP.md.
User: autonomous work, minimal sufficient checks, commit only completed milestones.

## Completed: QUICK-01 controls and held utility items

Q/E abilities;1/2 direct weapon sets; hold3/4 for two three-entry wheels, release to hold. F interaction. Any held utility stows both weapons; LMB uses only the held stack. Inventory hover/selection +3/4 assigns slots; right-click holds without consuming. Server enforces exact held identity and rejects weapon attacks. Changing hands cancels drinking. Owner/observer visuals and six-slot HUD are implemented; ammo pools survive utility use. Protocol12, saved belt schema5 migrates old4 entries into6.

Report: Tasks/QUICK_HANDS_20261111.md. Final build: Builds/Playable/20261111-162703-147/EmberdelveNet.exe. Focused EditMode20/20; affected schema/authority/visual/input/network proofs passed across recorded runs. Final SkillCooldown253/0; graphical quick-access UI139/0. Initial failures and their fixes are recorded honestly in the report, not presented as a green aggregate. Seed11 live host/client wheels, held potion/torch, remote light, Q/E,1/2,F verified; final HUD/wheel polish rechecked on final build. All review windows closed; main Editor untouched.

## Previous accepted baseline

LIGHT-01: inventory torch, source-based hand lighting and synchronized extinguish/relight of world lights; prior controls are superseded above. Simple flame/local scattering, no fuel or room-scale volumetrics. See Tasks/LIGHTING_TORCH_20261111.md.

Sword hilt lowered, upright35-degree ready retained, bow accepted. Whole CC0 Innerscene/MakeHuman source supplies class bodies and first-person hands. Open moonlit ruins have terraces/stairs, dormant-to-aggro undead and server-owned breakable barrels. See Tasks/LOW_SWORD_20261111.md, UNIFIED_HUMAN_20261109.md and MOONLIT_RUINS_20261109.md.

## Next actions and limits

1. Follow the next user-selected slice; no background full-suite reruns. Utility bottle/ammo models remain simple.
2. Separate existing work: sleeve seams, third-person bow draw state, crossbow model/reload, brief magenta impact material. Old full inventory UI tests still assume pre-paper-doll text rows; quick-access UI has its own focused proof and requires real graphics for the portrait.
3. Preserve/review inherited material/import/log/Pipeline drift separately. Never blanket-add it to commits.
