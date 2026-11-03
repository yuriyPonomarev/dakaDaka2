# CURRENT WORK — 3 Nov 2026

Rewrite this file each slice; history is in `Docs/Archive/`.

## State
- HEAD `91da51d1`: U3B3 network weapon combat + U3C1 blade trajectory/guard + V2 dark-retro visuals (KayKit skeletons, retro pass).
- Workflow reset (user 3 Nov): fast loop in the open Editor + `Tools/Run-All.ps1` on the mirror replace LLM-driven testing. See AGENTS.md.
- New tooling: `EmberdelveGateSuite` (all gates in one Unity process), `Tools/gate-sets.json`, `Tools/Run-All.ps1`, `EmberdelveSourceLint` (source-text checks moved out of gates, warnings only).
- NetCombat gate: 5 melee checks failed with "0 patterns tried" — fixed by re-resolving the weapon asset (`LiveWeapon`); confirm in the Run-All result.

## Decisions in force
- Sword-vs-sword block holds (`Weapon_AshenShortSword.impactResist 15`); the sledgehammer breaks guard.
- Guard endurance/stamina OFF.

## Next
1. U4A contested chests over the network (cloud draft exists: rebase onto HEAD, don't apply blindly).
2. U4B corpse, raid timer, extraction → playable build for the user.
3. U4C stash, then U3C2 (swing aim visual, observer warning).

## Blockers
- none
