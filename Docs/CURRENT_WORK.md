# DakaDaka — current work

Updated 9 November 2026. Rules: ../AGENTS.md. Roadmap: ROADMAP.md.
User: autonomous work, minimal sufficient checks, commit only completed milestones.

## Latest accepted milestone: MAP-01 crypt architecture

Task/evidence: Tasks/CRYPT_CONTENT_20261109.md. User authorized map content inspired by Dark and Darker after the accepted raid baseline7f203c9c.

- All10 saved room modules rebuilt with curved vaults/arches, colonnades, tomb alcoves/sarcophagi, chapel pew aisles, reliquary shelves/urns, cover and differentiated interiors. Existing10 IDs/order and four sockets preserved.
- Readable cold reflected light and warm braziers; blue extraction-room accent. Geometry lighting excludes hands. Existing materials reused; original generated arch mesh and flame materials, no external asset import or global render-setting change.
- Ordinary client movement/E crossed bridge and both doorway seams on seed11/hashCC18B0DC and seed2043687868/hash00B81D9C; final room screenshots reviewed. No teleport or combat acceptance claim from these no-enemy geometry sessions.
- Protocol6 prevents old geometry clients joining this version. Existing inventory/combat rules and loot/enemy systems unchanged. Build Crypt Rooms and Playable combines catalog+build in one Editor launch.

## Verification

- Level assembly29/29. Initial focused run20261109-110347-338 passed NetFinal172/0, NetDoor37/0, NetInventory146/0; NetMovement found a test-lane selection defect.
- Development-only movement proof mistook low footings/glancing columns for a stopping wall. Added chest-height, full-width head-on face validation without changing movement thresholds or gameplay collision. Focused fixture5/5.
- Corrected NetMovement190/0 in20261109-111129-372 on exact failure seed-986579934/hash620D25CB. Original failed run remains recorded; combined evidence accepts this milestone. Do not repeat passing tests just for a green aggregate.
- Scoped source/assets equal verified mirror; code/doc whitespace check passed. Commit uses26 explicit paths. Six inherited local-only paths remain outside it: two Stone materials, Assets/Logs.meta, connection-check screenshot/meta, Pipeline EditorPipelineConfig.json.

## Existing raid baseline and limits

Baseline7f203c9c accepted network raid, inventory/corpse/extraction/result/retry, hands/guard/enemy phases, HUD/countdown. Historical detail: Tasks/RAID_UI_ACCEPTANCE_20261108.md. Infrastructure and atomic equip were separate earlier commits.

Main user Editor untouched; builds/tests only ../DakaDaka_ci. Final graphical sessions closed v3 B12/A2 and v4 B12/A1. No test/build/player intentionally left running.

This is a first architecture/content pass, not new traps, secret routes or a complete art campaign. Persistent stash remains deferred; corpse loot is session-only. Default9 players/parser64 are not measured capacity; four-client proof exists, loss/latency and nine-client load remain future release coverage.

## Next steps

1. User playtest the new room scale, cover and visibility; address concrete findings as one slice.
2. If continuing map content, design encounter/loot placement and further room-specific routes as the next bounded milestone.
3. Keep stash/new systems and broader load/loss acceptance separate; do not restart the completed raid cleanup.
