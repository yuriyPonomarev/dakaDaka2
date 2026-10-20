# Asset plan and sourcing

19October2026: user explicitly authorized searching open-source textures/packs/assets. Codex curates downloads and licenses; local worker integrates only assigned files. No payments or external messages.

First inventory/reuse the existing packs at C:/Users/yrapa/Documents/just test/новый-игровой-проект/assets/vendor and original manifest.json. They include KayKit dungeon/adventurer/skeleton meshes/animations and Kenney audio. Do not redownload the same files. Import into a Unity vendor namespace only when the relevant stage is assigned; preserve licenses/hashes and use prefab adapters so collider/gameplay sizes remain stable. Inspect actual file formats before deciding whether Unity needs an importer or alternate source format; do not claim glTF is natively ready merely because Godot loaded it.

Verified author sources:
- KayKit Dungeon Remastered: https://github.com/KayKit-Game-Assets/KayKit-Dungeon-Remastered-1.0 — author README/license CC0-1.0, dungeon props/walls/chests, FBX/GLTF/OBJ listed. Already downloaded content is preferred.
- ambientCG textures: https://docs.ambientcg.com/license/ — downloadable assets are CC0-1.0. Candidate source for missing worn stone/wood/moss surfaces; no particular texture downloaded/selected yet.

For every imported pack/material keep a machine-readable manifest: source URL, creator, license text/file, pinned version/commit if applicable, downloaded/copied SHA256, relative Unity path, modifications and usage. Open-source code dependencies and art licenses are distinct: inspect package/code license as well as asset license. Final selection prioritizes a coherent dark stylized crypt, readable enemy/player/weapon silhouettes and restrained telegraphs. Unlicensed/ambiguous, ripped or merely free-to-view content is not a deliverable.

Integration passes: one floor/wall/torch room -> one animated enemy and both player classes -> weapons/chests/loot -> sound/music. Render-inspect each representative pass. Avoid a bulk import that hides broken materials or takes a model implementation attempt away from gameplay.
