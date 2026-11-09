# Human-proportioned grim player models — 9 November 2026

## Requested outcome

Replace the cartoon-like Warrior/Archer presentation with characters suited to the crypt setting. This is a bounded art/animation integration milestone, not photoreal character production or wearable-item appearance.

## Delivered

- Both class prefabs now use the existing Quaternius UAL1 CC0 human body and Humanoid avatar, replacing the chibi KayKit proportions. Original fitted meshes provide armour/clothes; the source KayKit assets remain on disk but are no longer referenced by these class prefabs.
- Warrior: closed steel helmet with visor/nasal/vents, cuirass, layered waist plates, pauldrons, bracers, greaves, belt/pouches and faded oxblood tabard. Archer: open-front hood, face wrap/cowl, leather cuirass/bracers, belt/pouches and a back quiver with arrows. Muted steel, charcoal linen, brown leather and brass; deterministic fine material grain.
- Generated art is versioned under Art/Models/GrimPlayers. Rebuild entry point EmberdelveClassVisuals.BuildAndPackage delegates to EmberdelveGrimClassVisuals; it cannot accidentally restore the old models. Human proportions and clip references reuse existing project assets; no downloads, packages or engine changes.
- PlayerAvatar supports the new Humanoid Animator while retaining legacy fallback. RemoteWeaponPresentation binds human hand/forearm/upper-arm bones through Humanoid mappings. Existing server-authoritative attack trajectories and effective guard remain unchanged. No colliders/root motion/network protocol changes; protocol8 retained.
- Inventory preview uses the same class prefab and a brighter isolated key/fill setup. Idle is a relaxed human stance; world movement, guard and death states use existing animation clips.

## Verification

- Checkpoint: Logs/harness/before-grim-models.json. Planner selected all70 conservatively for builder/prefab inputs; narrowed to compile, generated asset validation, Hands/Visual and actual host/client visual review. No combat/inventory/movement rules changed; prior network combat suites were not repeated.
- Build-v1 compiled but visual inspection found inward armour surfaces and an overly dark portrait. V2 fixed winding/lighting and neutral idle; inspection then exposed headwear authored in the FBX bind orientation rather than the live Humanoid orientation. V3 builds accessories in the evaluated idle frame and asserts that headwear faces forward.
- Final build-v3 PASS. Builder verifies both avatars, five animation states and no cosmetic colliders. Evidence: Logs/acceptance/20261109-grim-models/build-v3.log. Playable copied to Builds/Playable/20261109-grim-models/EmberdelveNet.exe; tested through the mirror stable net-final path.
- Seed11/protocol8, two real graphical windows: v3-host warrior inventory; v3-client archer inventory; remote bodies both directions; real client movement; sword LR/RL/overhead chain and raised shield; client bow-to-rapier swap and thrust. Logs confirm all four attack patterns. Screenshots in v3-host/snapshots and v3-client/snapshots. Final logs contain no exceptions/animation-state errors. Closed client12/host14.
- Focused run20261109-141624-941: Hands92/0, Visual207/0, both PASS. No gameplay suites repeated. Scoped assets/source match mirror; no surviving test players. Six inherited local-only paths excluded from commit.

## Limits

Low-poly grim fantasy, not photoreal/AAA character art. Fixed class outfits; equipped loot does not change the worn model. No facial animation, cloth simulation or new ranged draw/reload clips. Existing first-person arms and weapons remain separate. Visual acceptance covers the listed poses locally, not exhaustive pose clipping, internet conditions or capacity.
