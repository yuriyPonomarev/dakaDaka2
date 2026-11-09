# V5: skeletal enemy visuals

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


The four enemy visual prefabs use Grab3D's CC0 rigged Human Skeleton, reduced from 270,030 to 22,952 triangles with the reproducible Blender script in `Tools/OptimizeGrab3DSkeleton.py`. Source, hash, license and AI provenance are in `Docs/ASSET_PLAN.md`. The extracted original texture is imported point-filtered at 512px and tinted per variant. Quaternius Humanoid clips continue to drive the body; Minion and Rogue use a weapon-ready idle. The three armed variants use Sword_Regular_A, selected after comparing the previous clips and A/B in three phases; A had less extreme deformation. No combat rules or colliders changed.

The source rig has no finger bones. During prefab building, the weapon grip is estimated from vertices weighted to the hand in the intended grip pose. The mage's eyes are attached using an animated pose instead of the unrelated bind-pose orientation. Existing `BloodHit` remains on every prefab.

Unity Humanoid avatar valid, skinned mesh has 24 bones. EditMode EnemyVisualQuaterniusTests: 46/46; fast gates: 12/12 (`Logs/quick/skeleton_v5/summary.txt`). Preview frames: `Logs/quick/skeleton_idle_fixed.png`, `Sword_Regular_A_25.png`, `_50.png`, `_75.png` and `skeleton_walk.png`. These show authored poses, not an interactive in-game playthrough. The rig's fingers remain static. The short A clip hits the existing 0.6x minimum animation speed for longer windups, so its visual impact is not proven to coincide with authority damage; animation timing, grip, deforming joints and contrast need live review. Client-only network enemy proxies remain capsules. Full Run-All remains owed.
