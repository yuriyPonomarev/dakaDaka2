# Module assembly repair — 22 November 2026

The user rejected the previous visual result with a screenshot showing a floating arch, incorrectly fitted gate, broken masonry and depth artifacts. The earlier traversal recordings were insufficient visual acceptance. This slice repairs the existing saved modules; it does not claim a new room-layout family or exact Dark and Darker reconstruction.

## Root causes and implementation

- All 30 rooms had overlapping step render surfaces: 2,400 treads with 5/10 mm excess depth, plus 52.5 mm gallery-top overlap. Fitted derivatives preserve rounded noses and collider geometry while leaving 4 mm tread joints and at least 2 mm landing clearance.
- Sixty retaining-wall renderers duplicated the ground slab's upper side and top surfaces. The visual lower wall now ends at the slab underside, y = −0.6 m. Its original physics and floor height remain.
- Closed walls were visually replaced by grids of cropped column shafts. These visual children are removed and continuous rounded masonry surfaces use the existing world-space stone material.
- Neighbouring modules owned overlapping boundary surfaces. Closed render meshes now fit within their own module's ±18 m boundary, retaining the inner envelope and original topology. A discarded clipping prototype correctly rejected nested ornamental shell contours; no unreviewed open-cut result is shipped.
- Imported arch springlines are fitted to actual supporting columns instead of overall crown bounds. Two unsupported legacy ornaments (base Vault barrel and base Extraction arc) are omitted, including their overhead MeshColliders; the extraction gate retains its own frame. Other gameplay colliders stay unchanged.
- Door arch infill restores its exact authored aperture; jamb/infill walls restore continuous geometry. The original Meshy moving leaf is retained. Its hinge and blocker move together to a face mount at local z = −0.435 m: actual-mesh sweep covers 3,978,540 vertex/angle pairs with zero reveal intersections and a 5 mm minimum gap. Collider sizes, trigger and authority remain. Gate_Portal changes from a floating 2.4 × 3.1 × 1 m cube to a 2.3 × 3.4 × 0.06 m veil fitted floor-to-lintel. Only its scale changes in the three saved scenes.
- AGENTS, both project 3D/Meshy skills, art guide, Cursor rule and graphical review procedure now require geometry/depth separation, supported joins, doorway motion, adjacent module checks and rejection of visible artifacts even if automated checks pass. No renderQueue/ZTest/depth-bias workaround.

## Verification

Checkpoint: `Logs/harness/before-module-joins-20261122.json`. Shared scene/editor inputs conservatively select all 71 gates. Bounded matrix selection: Level EditMode, NetMovement, NetFinal, NetDoor and NetInventory because the repaired door hinge/blocker placement changes. No broad 71-gate rerun.

Independent serialized audit: all 30 root/component IDs and catalog references valid; 9,204 colliders retain geometry and ancestor transforms except the two explicit overhead collider disables. 83 script component documents and 1,307 nested prefab-instance documents unchanged; no newly dangling references. Evidence: `Logs/acceptance/module-joins-20261122/prefab-gameplay-invariants-audit.json`.

Final saved-mesh repair and validation succeeded. Level EditMode: 150/150 pass, no skipped cases, in `Logs/runs/20261122-081547-253` (127.74 s). NetMovement 198/198, NetDoor 49/49, NetInventory 148/148 and NetFinal 172/172 pass in the same run (4/4 gates, 471.1 s; whole run 666.2 s). The later backing-only repair changes one visual Transform and MeshFilter; all door collider/script documents are unchanged, so passing gameplay checks were not repeated solely for that visual surface.

Assembly review before the final hinge placement correction:
- Seed 12345: 3,056 progressed frames, 1280 × 720; ordinary gallery/undercroft walk (−1.47 to +3.93 m), then labelled free-camera map/door/gate inspection and four views of each of all 30 saved prefabs. Recording 3,055 encoded frames / 101.83 s, stereo 48 kHz, mean −25.4 dB, peak −13.3 dB. All 120 catalog frames reviewed through four contact sheets and selected full-size views. The indoor rooms remain dark; this is not a lighting/material redesign.
- Seed 67890: 1,106 progressed frames, same walking heights, 1280 × 720, normal map/door/gate views. Recording 1,105 frames / 36.83 s, stereo 48 kHz, mean −25.5 dB, peak −13.4 dB.
- Both runs: health 120/120, saved AI-off behavior, no player teleport or AI-deactivation helper. Videos fully decoded and delivered in this chat. Free-camera views are assembly inspection, not claims that the character walked all 30 prefabs. Normal player/camera/HUD/lighting were used for the walking section; inspection views retain scene lighting/HUD and are labelled.
- Recordings live in `Logs/acceptance/module-joins-20261122/live-<seed>-assembly/module-assembly-review.mp4`. The first result.txt uses the older generic helper description; the labelled video and this report distinguish walking from camera inspection.

Final door review:
- `live-12345-door-final` was rejected visually despite its mechanical PASS: the old backing left a large daylight gap behind the closed arch. That failed recording remains as evidence and was delivered in chat.
- Replaced only the visual backing with `ArchitecturalJoins/DoorBacking_ApertureInset5mm.asset`, derived from the actual segmented aperture by a uniform 5 mm inward edge offset, with a 5 mm bottom clearance. Its rear face lies 5 mm before the wall. The existing inherited `RustboundGate/DoorBacking.asset` was not edited. Closed 92-triangle mesh; analytic motion clearance and 961 sampled angles show zero wall intersections. Evidence: `door-backing-fit-audit.json` and `final-backing-prefab-delta.json`.
- Corrected `live-12345-door-final-sealed` and `live-67890-door-final-sealed`: each 148 progressed frames, 147 encoded frames / 4.90 s, 1280 × 720, living player health 120, AI=0. Four close views and moving-camera open/close recordings from both sides inspected. The oversized gap and reveal collision are corrected; the intended narrow 5 mm movement clearance remains. These are labelled free-camera door checks, not traversal tests.
- Both final MP4s fully decoded, motion contact sheets and all eight full-size door views inspected, stereo audio 48 kHz (mean −25.2 dB / peak −14.2 dB), and both videos delivered in chat. Audio was checked for a valid non-silent track, not independently assessed for artistic quality.
- Final primary/mirror SHA-256 comparison: 348 staged affected asset/editor files, zero differences (`final-input-comparison.json`). All owned review Editors exited and mirror ownership/lock release was verified; primary Editor PID 8352 remained open.

The repaired construction has bounded automated and visual proof. User acceptance is still separate; these checks do not claim every possible generated viewpoint, a complete lighting redesign, or a new module layout system.

The primary Editor stays open in its existing state. All live reviews use an owned mirror Editor and isolated saves. AI remains disabled in saved new-raid settings; ordinary-combat verification processes explicitly override AI on. Inherited material/Oasis/settings edits and the old stash are excluded.
