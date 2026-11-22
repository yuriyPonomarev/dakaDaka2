# DakaDaka — current work

Updated 21 November 2026. Primary development checkout: `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`, tracking `origin/main`. `../DakaDaka` is the preserved old checkout; tests/builds use `../DakaDaka_ci`. Do not close or enter Play mode in the user's Editor.

## Active slice: expanded rooms and lever caches

User requested larger, more varied modules, rises/descents, and secret rooms opened by levers, using existing assets or free public Meshy assets. Report: [ROOM_EXPANSION_20261121](../Tasks/ROOM_EXPANSION_20261121.md).

- All 30 saved room prefabs expanded from 24×24m to 36×36m. Two supported upper balconies at +3.9m, a true sunken passage at −1.5m, stairs, perimeter bays and varied furnishing. Free central doorway axis preserves large-enemy clearance. Three catalogs retain stable order; protocol 43 and dimension-aware socket/corridor placement.
- Existing CC0 Poly Haven tables/barrels/crates/buckets, project arches/sarcophagi and existing materials reused. Public Meshy downloads were blocked by site access; no new Meshy model or spending.
- Twelve cache variants: lever-only server reach/LOS, stable room/floor IDs, replicated sliding wall/handle, one-way unlock, AI cannot operate secrets. Cache volume excludes random spawns; authored anchors add deterministic server-owned chests.
- Final v6 main compile clean; mirror rebuilt all 30 modules. Thirty prefabs and three catalogs hash-match the mirror. Fifteen final views inspected/stored in `Logs/review/room-expansion` (staged renders, not ordinary live-player acceptance).
- Final v6 level tests: 146/146 passed, including all 18 new controller/clearance/AI path cases and multiple-seed staged-floor placement. Earlier full EditMode: 1461 tests, 4 failures, 12 skipped; failures concern skeleton meshes, Cinder Warden mapping, crossbow palm and cinder_club mapping. Affected planner, door, snapshot and movement-geometry fixtures passed.
- Full integration `Logs/runs/20261121-192920-176`: 71/71 gates completed, 65 passed, 6 failed; all network entries passed. Remaining failures concern inventory schema, claw trajectory, weapon/preset mapping and HUD (report lists details).
- Final affected network run `Logs/runs/20261121-201826-956`: 5/5 passed, 924 checks and no failures: NetMovement 198, NetFinal 172, NetDoor 49, NetInventory 148, NetRoyale 357. NetDoor includes actual remote-wall rejection, accepted lever RPC, three-client wall replication and repeated-activation rejection.

Changes remain uncommitted pending user visual acceptance. Preserve inherited material, Oasis texture and ProjectSettings drift; no package/engine upgrades.

## Preserved work and limitations

Previous CURRENT_WORK, including detailed 17–20 November combat/character evidence and historical references, is preserved verbatim in [CURRENT_WORK_BEFORE_ROOMS_20261121](../Tasks/CURRENT_WORK_BEFORE_ROOMS_20261121.md). Historical results do not validate this slice. Meshy warrior/hands and two-handed guard visual acceptance remain separate. Ordinary Royale final evacuation/profile-restart acceptance and previously observed native allocator error investigation remain open. Several older task links point to files retained only in the old checkout.

## Next three actions

1. Review final room previews with the user; choose any requested visual adjustments.
2. Review expanded modules and lever caches in ordinary play with the user; visual acceptance remains separate from automated proof.
3. Resume existing Royale acceptance and deferred character/combat feedback according to the user's next priority.
