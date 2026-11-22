# DakaDaka - current work

Updated 22 November 2026. The ONLY development checkout is `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`. Legacy `../DakaDaka` is read-only; `../DakaDaka_ci` is the disposable verification mirror. Keep the user's primary Editor open. Reviews use isolated saves and owned mirror Editors; record/deliver MP4 with game audio.

## Completed slice: local light for furnished rooms

Report: [LAYOUT_LIGHTING_20261122](../Tasks/LAYOUT_LIGHTING_20261122.md). All 30 module prefabs now have layout-aware local illumination: 60 existing table candles lit, 99 shelf candles seated on actual boards, and 81 existing Meshy wall sconces mounted to chamber/storage/chapel masonry. Warm point pools, subtle candle flicker, bounded torch range and soft shadows retain dark surroundings. Final point sources correct the intermediate spotlights that left mounts/backing walls black. Original downloads/materials and global lighting/exposure are unchanged; no new colliders or furniture displacement. The furnishing builder reapplies this saved lighting pass.

The mandatory local extracted Dark and Darker reference was inspected for instantiated wall candles and local source structure. Exact paths and decoded facts/limits are in the report; placements and photometry are our original adaptation. Public Meshy lantern/torch/candle candidates were researched and linked, not downloaded or claimed game-ready.

## Verification

Existing WorldLightInteractableTests: 6/6 passed (run20261122-180118-761); an initial wrong namespace correctly failed with zero tests. Final Visual: 216 checks, zero failures (run20261122-182559-515). Exact original component/transform preservation and source/mirror hashes verified across all 30 prefabs.

Final native recordings: seed12345 lighting-v3 (4m22.70s, all 30 saved modules) and seed67890 lighting-final (1m32.30s, second generated map). Both include ordinary Entry chamber/dais walking and actual candle off/on, then labelled free-camera inspection. No diagnostic lighting, health120, AI0. Reviewed furniture, source contact, wall backing and sampled motion across all three catalogs; no blocking lighting finding. Videos fully decoded with non-silent stereo audio and delivered, alongside intermediate recordings. Scope/limits and evidence paths are in the report.

Only lighting-owned files are committed; inherited 15 material/model/Oasis/settings changes and the old stash remain untouched. No push. The primary Editor remains open; owned mirror reviews are closed. Reload Play/start a new raid to see saved prefab changes. Bots remain disabled in new raids and later floors until explicitly restored.

## Preserved baseline and limitations

Furnishing/two-sided doors commit6f00729f8: [LAYOUT_FURNISHING_20261122](../Tasks/LAYOUT_FURNISHING_20261122.md), 669 objects fitted to ten circulation families, repaired tomb bypass and door core/rim. Level213/213 and network567 checks passed; native two-seed evidence retained. Earlier layout/lobby b59f72dc5: [LAYOUT_FAMILIES_20261122](../Tasks/LAYOUT_FAMILIES_20261122.md). Integration19519855b preserved larger rooms, vertical routes and lever caches; protocol47. Earlier integration had65/71 gates with baseline failures; current focused proof does not declare the entire repository green.

Early ambience review may have parked Squire prepared gear in stash; backup/evidence remains in Logs/acceptance/ambience-20261122/profile-preservation, user already informed. Do not guess a loadout restoration. No engine/package upgrades.

## Next three actions

1. User reviews a freshly loaded raid with AI still disabled.
2. Address concrete visual feedback using affected native recordings and focused checks; preserve failed evidence.
3. Resume authorized unfinished gameplay work from ROADMAP; restore raid AI only on explicit request.
