# Class models and ability icons — 9 November 2026

Completed requested slice: eight active/passive icons, distinct Warrior/Archer models in the character panel and in the world.

## Implementation

Original generated engraved-silver4x2 icon atlas; cached sprites indexed by existing skill IDs. CharacterSidebar uses48px icons alongside names/effects. The gameplay HUD text strips are unchanged.

Class assets reference full KayKit models, not mannequin recolours. Knight for Warrior, Rogue_Hooded for Archer. Original CC0 files/license/checksums retained; source/provenance in ASSET_PLAN.md. Converted with portable Blender4.2.9 using Tools/Convert-ClassModels.py. Optional source weapon variants excluded to prevent all accessories appearing simultaneously. No Unity package or engine upgrades.

PlayerAvatar follows the existing rig position/heading and switches Idle/Walk/Run/Block/Death clips. No added colliders, gameplay authority, root-motion movement or network protocol. Locally controlled body renderers are hidden; other players see the class body. Portrait reuses the class prefab and its material. Outfits are fixed class visuals; equipped-item appearance, held world weapons and combat attack synchronization are not part of this slice.

## Verification

Checkpoint Logs/harness/before-class-visuals.json. Planner conservatively selected70 gates for shared class/rig/asset inputs. Narrowed to compilation, imported clip validation and actual host/client visual/input review: no movement/combat/item rule or collision changes. No repeat of prior green gameplay suites.

Evidence under Logs/acceptance/20261109-class-visuals, seed11/protocol7, 1280x720:
- Initial build and v2 stopped before player build: existing KayKit prop importer disabled Adventurers animations. Added an explicit Adventurers branch; existing skeleton and prop branches retained.
- v3 compiled; graphical review caught missing icon initialization and model import scaling. Fixed icon initialization and conversion unit scaling.
- v4 compiled and icons worked; graphical review exposed overlapping optional accessories from source GLBs. Filtered accessories during conversion.
- build-v5.log PASS; exactly5 imported runtime clips per class.
- v5-host/snapshots/warrior.png and v5-client/snapshots/archer.png: correct different models, all8 correct icons, live class stats/skills.
- v5-host/snapshots/remote-archer-full.png and v5-client/snapshots/remote-warrior-full.png: remote class bodies visible in both directions; local first-person view unobstructed.
- v5-host/snapshots/remote-moving.png: client actual D-key movement observed from host, changed world position and limb pose. No exhaustive combat/animation acceptance claimed.
- Final host/client logs checked: no exceptions, animation-state-not-found or playback errors.
- Closed review client8/host11. Main Editor untouched. Generated model/icon import metadata and class references copied back from mirror; unrelated six local-only paths preserved.

Licensing: official KayKit CC0 repository, no paid assets. Icons are original generated artwork. Tools and downloaded converter/archive in ignored acceptance folder are not shipped or committed.
