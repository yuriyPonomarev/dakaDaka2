# Unified human body and first-person arms — 9 November 2026

User requests a complete realistic character whose hands also supply the first-person view. This supersedes the separate FPS-arms candidate. Scope: one CC0 body/rig for Warrior and Archer, class equipment retained, shared arms derived from the exact same skin, existing animation library retargeted. No combat, network protocol or collision changes.

Source: Innerscene male base mesh, adapted from MakeHuman CC0. Source GLB and embedded copyright retained under Art/ThirdParty/Innerscene/Human with license and hash manifest. Despite source page's texture statement, binary inspection found no texture images. Existing project materials supply clothing/skin. Blender conversion reduces 109k triangles to 34,146; all 30 finger bones and detailed hand vertices retained. One body renderer includes eyes; no independently sourced arms.

Implementation: native MakeHuman skeleton mapped to Unity Humanoid; existing arm builder selects its upperarm/lowerarm/wrist/metacarpal/finger vertices; class builder uses same source with class armour. Original source is not a finished dressed character. Conversion reproducible with Tools/Convert-UnifiedHuman.py.

Checkpoint: Logs/harness/before-unified-character.json. Completed integration acceptance:
- Avatar valid/humanoid, all30 fingers mapped. Import emits a metacarpal in-between-rotation warning; no missing required joints. Retargeted hand poses reviewed rather than claiming warning-free import.
- Renders: DakaDaka_ci/Logs/grip-review/classes.png, bow-1.png and ashen_short_sword.png. Fixed offscreen skin update, face material classification, helmet/hood height, and closed boots. Mesh regeneration explicitly uploads updated data (otherwise previews can retain an old GPU mesh).
- EditMode WeaponViewModelTests34/0: Logs/runs/unified-human-editmode. Hands92/0, Visual207/0 and playable build PASS: Logs/runs/20261109-170031-385. Broad unknown-source planner result narrowed to actual visual scope; no combat rules, inventory state or network protocol changes; no full suite rerun.
- Actual input/observer review: Logs/acceptance/20261109-unified-human, seed11/protocol9. First-person bow draw/release, sword attack/block, Q swap-return; warrior inventory portrait; remote warrior attack and archer movement inspected. Screenshots include warrior-inventory, bow-draw, sword-attack, archer-front, archer-moving and warrior-attacking. No exception/error lines found. Both owned windows closed; user's main Editor untouched.
- Build: Builds/Playable/20261109-170031-385/EmberdelveNet.exe. Main generated prefabs/meshes copied back from tested mirror; source files agree. Generated whitespace-only material drift removed without semantic change.

Limits: this replaces anatomy and unifies source geometry; existing armour remains original simple geometry. Fixed outfits do not react to inventory armour slots. Skin uses project procedural materials, not photographic textures. Jagged sleeve material boundaries, strong torch overexposure, generic closed-finger bow grip and absent third-person bow draw-state replication remain visual polish tasks. No claim of final art quality.
