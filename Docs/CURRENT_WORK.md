# DakaDaka — current work

## Development checkout (16 November 2026)

The primary checkout for all development and agents on this machine is `C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff`, tracking GitHub `origin/main`. The prior `../DakaDaka` checkout is preserved with a different Git history and is not the development source. The handoff WIP is committed as `94471be3` in the rewritten history; six material changes and local-only files were excluded and remain in the old checkout. Historical logs and builds referenced below may exist only in the old checkout or `../DakaDaka_ci`; their results do not establish a fresh run in this checkout. Use `../DakaDaka_ci` as the test mirror, with the primary checkout as source.

Updated 17 November 2026. Active slice is the user-requested Meshy warrior and bare first-person hands, followed by feedback requesting a more muscular physique and healthier wrist poses. Tasks/MESHY_CHARACTER_20261117.md records assets, charges, current visual limitations and proof. The latest completed slice before this work was ground loot models (Tasks/GROUND_LOOT_MODELS_20261113.md), following the torch reference pass (Tasks/TORCH_REFERENCE_FLAME_20261113.md). Historical UI/combat/stash/Royale/art work is recorded by the handoff WIP commit described above.

## Latest implementation and proof

Cinder Warden is installed in the Modular Crypts elite slot. The boss starts awake with 320 HP and has an authoritative, animation-sampled club sweep. Existing raid AI and loot rules drive the encounter. `EnemySweep` passed 121 checks including 13 boss checks, and the enemy wire fixture passed 22 checks. `CinderCrypt` verified a real player hit in the saved scene (120 to 61.5 HP after the warning). `NetCombat` and `NetFinal` also passed. See [Cinder Warden task report](Tasks/CINDER_WARDEN_CRYPT_20261117.md).

Meshy warrior WIP now uses the314,833-triangle detailed Meshy7.1 muscular body, 4K color/normal maps and seven reused Meshy clips. Two server rig results omitted fingers despite verified requests. Local Blender repair preserves the generated surface/body rig and adds30 mapped finger joints with seam-consistent skin weights. The active first-person mesh derives from this same source:28,022 vertices/50,560 triangles, authored closed-hand/bow-hook rotations and rig-specific grip/scale/elbow settings. New assets and references are in the development checkout. Total spending116 credits, verified balance979. Visual EditMode v22 passed186/187, with only the same legacy enemy-derived-mesh mismatch. Run20261117-202622-322 passed Hands92, Visual216, NetCombat396, ProjectileBlock191 and SweptMelee674 in258.8s. v23 compiled and rendered actual24-frame body and first-person sword motion previews; equipment and motion inspected. Owned test processes exited; primary Editor untouched. Tasks/MESHY_CHARACTER_20261117.md records evidence and pending visual acceptance. Changes remain uncommitted during user review.

Ground loot: all24 item definitions covered, 15 new saved world entries (treasure, ammo, ranged weapons and reused melee/shield), six existing armor/torch entries and three runtime consumables. Invisible interaction proxy/custody unchanged. Hidden serialized visual cache avoids scene-reload duplicate models; authored recovered ammo suppresses legacy shaft boxes. Inventory icons unchanged in this slice.

Build20261113-223941-399 PASS; Visual216/0, NetInventory148/0; all24 Configure twice coverage passes, main compile clean. Gallery and actual seed1344668317 crossbow/reliquary view, ordinary F pickup accepted by server, bag entry and world removal verified. Logs/acceptance/loot-models-20261113. Test players closed. Known allocator error recurred; not fixed. At the time of this proof there was no mixed-tree commit.

Torch now uses one transparent animated volume with original tileable 3D noise: folded hot sheets, translucent gaps, fire around cloth head; sparks/smoke remain auxiliary particles. Runtime anchor follows actual hand pose. Light4.5/range12 preserved. Old five-system leaflet appearance is superseded, not claimed accepted. Body/ground/icon unchanged in this slice.

Final playable Builds/Playable/20261113-215305-744/EmberdelveNet.exe PASS; main compile clean. Visual216/0 and Hands92/0 in214810-676 before final cosmetic lower-density/radiance correction; final correction verified by fresh build/live review. No full-suite claim. Seed1344668317 first-person movement/turn, observer flame/light and stow removal visually checked. Evidence Logs/acceptance/torch-volume-20261113/final-seed-1344668317. Short prior-version wall-clock p50 visible3.916ms/stowed3.732ms on RTX5070 with two clients; not GPU or16-player proof. User judges likeness to photo; no exact-copy claim.

Native ThreadsafeLinearAllocator invalid-pointer error recurred in final owner, predating this slice. Deferred memory investigation remains open; runtime is not error-free. Dedicated review players shut down; main Editor untouched.

## Preserved prior work

Steel cuirass correction: shaped breast/back plates, tapered waist, rolled rims, closures and fauld lames; stable boiled_cuirass ID/stats, Steel Cuirass display. Tasks/STEEL_CUIRASS_20261113.md. Build165256-037, Visual218/0, live two-client equip/remove/walk/torchlight review. Other armor models/catalog/icons and owner/observer equipment views retained (Tasks/ITEM_MODELS_20261113.md, protocol41). Armor build161307-136; Visual216, Hands92, NetCombat397, NetInventory148, NetFinal172 passed plus focused AvatarArmorWire9/9 and Visual186/186. Cloaks bone-following without cloth; helmet/trouser baseline unchanged.

Prior torch body/model/icon and light work: TORCH_POLISH_20261113 and TORCH_WRAP_20261113. Their particle appearance is replaced by current volume. Environment materials/uneven floor/props/moon/stars/clouds retained: ENVIRONMENT_SURFACES_20261113, FIRE_FLOOR_20261113, ROOM_PROPS_SKY_20261113. Free/CC0 and original asset provenance in ASSET_PLAN; no Dark and Darker assets imported.

Physical six-pocket belt complete (three on3, three on4): potion/bandage cap5, torch1, actual bag custody, server moves/merges, profile schema6. Tasks/PHYSICAL_QUICK_BELT_20261113.md. Combined1391 EditMode cases after focused corrections, not fresh full aggregate. Relevant Hands/Loadout/NetInventory/NetExtraction/NetFinal/NetRoyale and graphics UI passed; isolated preparation restart preserved items.

Royale: three maps ruins→crypts→infernal; solo16, duo16, trio15, temporary destination bots; cross-session human matchmaking absent. Shrinking zone and three randomized party portal waves, last at final60s. Controlled floor custody/cleanup/extraction passed; ordinary full-match final evacuation/profile restart acceptance remains open (Tasks/SQUIRE_ROYALE_20261113.md).

## Next three actions

1. Review the installed detailed Meshy warrior and bare first-person hands with the user; all five focused gates passed, final equipment and motion previews are available. User visual acceptance remains pending. See Tasks/MESHY_CHARACTER_20261117.md.
2. Finish Royale ordinary full-match final evacuation/profile restart acceptance; controlled fixtures do not replace it.
3. Return to explicitly deferred native memory investigation when requested; preserve logs, no engine/package upgrades or unrelated drift cleanup.


