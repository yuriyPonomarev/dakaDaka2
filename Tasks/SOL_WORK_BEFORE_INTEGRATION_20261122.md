# DakaDaka — current work

Updated 21 November 2026. Offline Squire Royale uses the shared three-floor map sequence and loot screen. Preserve inherited uncommitted UI/combat/stash/Royale/art work. Main Editor stays open. The root runs mirror verification.

## Latest implementation and proof

The courtyard brazier uses the original textured bowl and its four sealed feet. A 13.5 cm trim removes the fused circular slab and seats the feet directly on the 90 cm ashlar pedestal. The flame and bowl proxy follow the lower rim. The collision footprint, pedestal and light identity are preserved.

Solo seeds 1344668317 and 8734652 passed capstone contact, material, flame, collision and connected navigation checks. Inspection and normal courtyard frames are in Logs/acceptance/brazier-without-base. Run 20261122-000229-746 passed NetMovement (198 checks) and NetFinal (172 checks). The focused MovementProofGeometry EditMode run passed all 5 cases.

The courtyard brazier now sits on a 90 cm ashlar pedestal with staggered joints, an irregular square cap and chipped blocks, rebuilt from the supplied reference. The game uses the original 1,736-triangle pedestal mesh and 2K Rock030 maps. The original collision components and lights compare byte for byte with the previous room prefab.

Solo seeds 1344668317 and 8734652 passed bowl seating, flame, collider and connected navigation checks. Seed 1344668317 was repeated after the 2K material import. The studio and courtyard frames are in Logs/acceptance/weathered-brazier-pedestal/final-2k.

The carved Meshy brazier sits on a 90 cm stone column near the sarcophagus arch in the entry courtyard. The bowl keeps its original 2K texture, and the existing flame and light sit in the basin. The fit updates the four existing collision pieces at that station, preserving the base footprint, room sockets and light identity.

Solo seeds 1344668317 and 8734652 passed room assembly, connected navigation, bowl collision, texture, pedestal contact and live flame checks. The final lighting pass was rendered in the first seed. Screenshots are in Logs/acceptance/entry-brazier. Run 20261121-223130-953 passed NetMovement (198 checks) and NetFinal (172 checks). The focused MovementProofGeometry EditMode run passed all five cases.

The three Meshy community models are available as textured prefabs. The entry courtyard displays the timber panel beside the west wall, ruined arches along the north wall, and the overgrown arch above the sarcophagus. The trial keeps the 173 authored collider blocks intact and switches the visible arch and column renderers in that bay. The full room builder and the **Apply Meshy Courtyard Trial** menu both use the saved prefabs.

Solo reviews on seeds `1344668317` and `8734652` confirmed a single trial courtyard, three fully bound materials, valid doorway sockets and connected navigation. In-game angles and a separately lit inspection frame are in `Logs/acceptance/community-ruins-fit`.

Rustbound doors use two original spatial cues through the existing world audio pool. A hinge creak starts with an opening swing. A low wooden impact plays when the moving leaf reaches its closed pose. Restoring a pose and repeating a state update are silent.

The live Solo review on seed `1344668317` verified both timings, one voice per action, the sound origin at the door and silent replay of both states. The two emitted clips are recorded in `Logs/acceptance/door-audio`.

Run `20261121-212734-204` passed NetDoor (37 checks) and exported `Builds/Playable/20261121-212734-204/EmberdelveNet.exe`.

Modular dungeon passages now use a 1.72 × 2.32 m arched prefab with the Rustbound grille from the user's archive and an original wooden leaf. The smaller passage is offset to keep a navigation lane through the existing socket. The leaf swings over 0.43 seconds. Network snapshots restore the finished pose, and subsequent confirmed state changes animate. Protocol 46 keeps door collision compatible across players running the same version.

Solo reviews with seeds `1344668317` and `8734652` confirmed connected navigation and opening and closing with F. Both directions passed intermediate and final hinge-angle checks. Previews and in-raid frames are in `Logs/acceptance/small-doors`.

Run `20261121-190125-740` passed NetDoor (37 checks) and exported `Builds/Playable/20261121-190125-740/EmberdelveNet.exe`. NetRoyale Trio passed 116 checks in run `20261121-190429-485`, including geometry and synchronized door state on floors two and three.

The nine outdoor Moonlit Ruins room prefabs have folded wall footprints, broken crowns and two chamfered corners each. The closed vault, entrance sockets, placement anchors and furnished objects retain their authored transforms. Rebuild the masonry with **Emberdelve → Visual → Rebuild Ruined Room Walls**.

Solo reviews on seeds `1344668317` and `8734652` passed. Each map assembled nine rooms and nine doors on connected navigation. The corner probes hit the diagonal stone inside the former square perimeter, and every new wall uses its visible mesh for physics. In-game close and elevated views are in `Logs/acceptance/room-shapes`.

An ordinary full block plays a short metal clash with one blue contact flash. A confirmed melee parry plays a higher ring with a burst of outward golden sparks. The authority uses the same blade-region decision as the riposte rule and includes the verdict in combat hit reports in protocol 45. Both cues keep the damage vignette off when HP stays the same.

Run `20261121-180341-514` passed MultiPlayer (149 checks), ProjectileBlock (194) and the dedicated NetCombat parry scenario (59). The local fixture checked the selected audio clips, the parry burst and the HUD. The physical blade fixture checked ordinary and parry-region contacts, and both network clients received the same zero-damage parry report exactly once.

The live dungeon review at seed `1344668317` triggered each cue separately, captured the blue contact and gold burst, and exported both emitted clips for verification. Evidence is in `Logs/acceptance/defense-cues`. The playable Windows build is `Builds/Playable/20261121-180341-514/EmberdelveNet.exe`.

Damage presentation starts the red HUD vignette only for positive resolved damage. A fully absorbed hit keeps its block cue without damage tint or a hurt sound. Hits through a broken guard still show the damage cue when they cost HP.

Run `20261121-174603-251` passed MultiPlayer (144 checks) and CombatFlow (599). The HUD fixture checked zero damage with and without the block flag and a broken guard that cost HP. CombatFlow checked actual guard absorption and break resolution. The playable Windows build is `Builds/Playable/20261121-174603-251/EmberdelveNet.exe`.

In offline and online DUO/TRIO, a player who dies can follow living allies from third person across floor transitions. The camera follows the ally's current floor while the player keeps their original death result. Tab switches allies. Results opens the player's own result and Spectate team resumes observation. When no ally remains, the result opens automatically. Network vitals carry the extracted flag in protocol 44 so observers also recognize teammates leaving through the final exit.

Run `20261121-172907-527` passed 1,403 EditMode tests and 372 NetRoyale checks. The online DUO death scenario followed the same surviving host through floors 1, 2 and 3 while the client's own death remained on floor 1. The client's original result opened when the host extracted.

The same run passed NetFinal (172 checks) and produced `Builds/Playable/20261121-172907-527/EmberdelveNet.exe`.

The live offline TRIO review at seed `1344668317` verified Tab switching, opening and resuming from the result screen, following both descents, and returning to the original result after the last surviving ally died. The review staged portal waves, deaths and positions. Evidence is in `Logs/acceptance/death-spectator`.

In offline and online DUO/TRIO, hold F to enter a red portal. The player then follows living allies on that floor with a third-person camera. Tab switches between eligible allies. The party descends together once each teammate has entered or died. Entrants keep their rigs, parties and loot. Online the authority validates activation and checks destination readiness for every entrant. Floor movement replication carries spectator state in protocol 43.

Run `20261121-170032-931` passed all 1,403 EditMode tests and the 363 NetRoyale checks. The host and remote client took turns entering the portal first on the two transitions in DUO and TRIO. The spectator camera followed a living ally until the party could leave, and the entrants kept their original equipment and loot. The focused team fixtures also cover descent after the last ally dies while the source is open.

The same run passed NetMovement (198 checks) and NetFinal (172) and produced `Builds/Playable/20261121-170032-931/EmberdelveNet.exe`.

The live offline review passed both transitions from the DUO and TRIO lobby at seed `1344668317`. With staged portal waves and teammate positions, actual F input started and canceled activation. The camera followed and switched between allies with Tab, then returned to first person after the party moved. On the second TRIO descent the last ally died while the source was open, and the two entrants arrived together. Evidence is in `Logs/acceptance/team-portal-spectator`.

Offline SOLO uses the same destination generator as online. Entering a red portal processes the local descent on the input frame. The next floor starts as soon as its map and roster are ready, bringing along any bots already in transit. Portal placement uses a separate random stream for each floor and raid, and searches alternate valid placements if the first picks conflict. The offline HUD displays the zone and portal waves and reads the active floor for room names. The final gate requires a continuous hold and cancels on movement, damage or release.

The live SOLO regression for seed `1344668317` recorded both destination arrivals in the F-input frame, with 0.39 s and 0.37 s of measured wall-clock preparation. It also verified survival of an already-descending bot, exact player loadout, old-floor retirement and final profile settlement. Evidence is in `Logs/acceptance/instant-offline-descent`.

Run `20261121-064729-196` passed NetRoyale (357 checks). The playable Windows build is `Builds/Playable/20261121-064729-196/EmberdelveNet.exe`.

Offline chests, defeated monsters and fallen bots use the network raid's paired loot screen with local authority. Corpse gear moves with its original item identity, and source-floor loot is retired after descent. The initial Unity route from Lobby used regular F input on both portals and the final gate with staged wave times and positions. The same player and bot survived the move, exact carried gear reached floor three, extraction saved the isolated profile, and a second seed changed the physical portal positions. Evidence is in `Logs/acceptance/offline-royale-parity`. Run `20261121-062159-529` passed 1,403 EditMode tests and NetRoyale, NetMovement, NetFinal, NetDoor and NetInventory before the shared loot screen was added.

Final run `20261121-063639-250` passed NetRoyale (357 checks), NetInventory (148) and NetExtraction (136). The playable Windows build is `Builds/Playable/20261121-063639-250/EmberdelveNet.exe`.

Melee bots use the authority's swing clock to move their torso and weapon and to draw the attack direction during windup. The observer receives the bot flag in the combat pose, which uses protocol 42. The offline F8 panel uses the raid's enemy definitions, four chest appearances and item catalog to place real enemies, chests and pickups on unobstructed floor ahead of the local player. Escape restores player control.

The offline Unity review verified all three spawn categories, input capture and release, and the warrior's torso, wrist and blade movement and correctly aligned warning on an accepted swing. Evidence is in `Logs/acceptance/bot-tells-dev-spawn`. Run `20261120-233835-744` passed all 1,401 EditMode tests and Visual, Hands, NetCombat, NetRoyale and NetInventory. The playable Windows build is `Builds/Playable/20261120-233835-744/EmberdelveNet.exe`.

Map placement is authored in the default modular raid. The three altars were resized, and 12 other room prefabs now have figures at the entrances and ossuaries and lit votives near extraction. Kneeling figures measure 2.10 m and standing figures 2.22 m against the 1.80 m character controller. Rich chest visuals alternate in the authored spawn list, so both archive variants appear. Two seeds per floor verified all four chest meshes with working hinge bindings, all nine wall sconces, lit candles, exact scale, and clear spawns in the assembled maps. Evidence is in `Logs/acceptance/map-dressing-placement`.

Run `20261120-211846-092` passed NetFinal (172 checks) and NetRoyale (357 checks). The playable Windows build is `Builds/Playable/20261120-211846-092/EmberdelveNet.exe`.

The shrine statues use dark weathered stone with baked fold occlusion, localized textured moss on the bases and robes, and branching fissures. Each plinth has three small modeled chips. The standing figure has a diagonal fracture through about half of its head. The cobweb renders and assets were removed from the game. Unity verified all three saved shrine rooms and rendered the surface detail in `Logs/acceptance/shrine-surface-detail`. Focused Visual run `20261120-205229-199` passed 216 checks.

The shrine altars now use original tileable weathered stone on both statues, with pores, fine cracks and a matte dark finish. The votive stand uses three animated flames and a shared warm flickering light. Unity verified the reusable prefabs in all three shrine rooms and rendered two candle phases in `Logs/acceptance/aged-shrines`. The focused Visual run `20261120-202623-309` passed 216 checks. The conservative plan routed new art paths to all gates. This pass changes only materials, candle geometry and lighting.

Map dressing uses four user-supplied chest models with separately hinged lids and wooden interiors. Supply, Ember and Sealed keep their existing loot tables, with an alternate Ember model selected by stable chest ID. The archive wall torch uses the existing flame and light in three raid scenes. The 30 modular rooms have a shared wall sconce prefab, and the three shrine rooms have both praying statues and the votive candles on their altar. Sources, mesh counts and texture settings are in the [art manifest](../SourceArt/AI/Meshy/MapDressing/manifest.json). The authoring run is in `Logs/acceptance/meshy-map-dressing/saved-map-migration.log`.

Unity 6000.2.5f1 mirror run `20261120-200627-405` passed EditMode 1400/0, Visual 216/0, NetInventory 148/0 and NetRoyale 357/0. The conservative harness plan selected 71 gates because of the new art paths. This slice used the visual, inventory and floor coverage because placement and collision for loot and players were preserved. The final chest liner inset was checked in Unity renders in `Logs/acceptance/meshy-map-dressing`. RetroVisual run `20261120-201529-742` passed all chest and wall-sconce checks, with one failure from the legacy particle-only expectation for the separate handheld `pitch_torch` effect.

Crypt props source pass delivered two chest concepts, two praying statues and an iron votive tray in FBX and GLB with 2K PBR maps. Five thumbnails were reviewed and triangle counts match between exports. The [source manifest](../SourceArt/AI/Meshy/CryptProps/manifest.json) records ten Meshy tasks, SHA-256 hashes and 75 credits spent. The archive chests are the game versions. The source-pass statues and candles are used on the shrine altars.

Cinder Warden visual prefab imported from Meshy with a 3.3 m Humanoid body, rigid ironbound club, pipe smoke and five clips. Editor sampling verified all five motions and produced idle and attack renders in `Logs/acceptance/cinder-warden`. Standalone prefab, pending user visual review and any future encounter hookup. Provenance and spend are recorded in ASSET_PLAN and the SourceArt manifest. The harness selected every gate because this new art path is unmapped. The isolated prefab was checked directly in Unity.

AI 3D prompting: project-local brief, Tripo and Meshy skills; thematic Cursor rule; source register and original crypt/weapon/character prompt examples in Docs/Art/AI_3D_PROMPTING.md. Documentation only: no generated assets, credit spending, package changes or runtime acceptance claim. Validation evidence is recorded in the task report.

Ground loot: all24 item definitions covered, 15 new saved world entries (treasure, ammo, ranged weapons and reused melee/shield), six existing armor/torch entries and three runtime consumables. Invisible interaction proxy/custody unchanged. Hidden serialized visual cache avoids scene-reload duplicate models; authored recovered ammo suppresses legacy shaft boxes. Inventory icons unchanged in this slice.

Build20261113-223941-399 PASS; Visual216/0, NetInventory148/0; all24 Configure twice coverage passes, main compile clean. Gallery and actual seed1344668317 crossbow/reliquary view, ordinary F pickup accepted by server, bag entry and world removal verified. Logs/acceptance/loot-models-20261113. Test players closed. Known allocator error recurred; not fixed. No mixed-tree commit.

Torch now uses one transparent animated volume with original tileable 3D noise: folded hot sheets, translucent gaps, fire around cloth head; sparks/smoke remain auxiliary particles. Runtime anchor follows actual hand pose. Light4.5/range12 preserved. Old five-system leaflet appearance is superseded, not claimed accepted. Body/ground/icon unchanged in this slice.

Final playable Builds/Playable/20261113-215305-744/EmberdelveNet.exe PASS; main compile clean. Visual216/0 and Hands92/0 in214810-676 before final cosmetic lower-density/radiance correction; final correction verified by fresh build/live review. No full-suite claim. Seed1344668317 first-person movement/turn, observer flame/light and stow removal visually checked. Evidence Logs/acceptance/torch-volume-20261113/final-seed-1344668317. Short prior-version wall-clock p50 visible3.916ms/stowed3.732ms on RTX5070 with two clients; not GPU or16-player proof. User judges likeness to photo; no exact-copy claim.

Native ThreadsafeLinearAllocator invalid-pointer error recurred in final owner, predating this slice. Deferred memory investigation remains open; runtime is not error-free. Dedicated review players shut down; main Editor untouched.

## Preserved prior work

Steel cuirass correction: shaped breast/back plates, tapered waist, rolled rims, closures and fauld lames; stable boiled_cuirass ID/stats, Steel Cuirass display. Tasks/STEEL_CUIRASS_20261113.md. Build165256-037, Visual218/0, live two-client equip/remove/walk/torchlight review. Other armor models/catalog/icons and owner/observer equipment views retained (Tasks/ITEM_MODELS_20261113.md, protocol41). Armor build161307-136; Visual216, Hands92, NetCombat397, NetInventory148, NetFinal172 passed plus focused AvatarArmorWire9/9 and Visual186/186. Cloaks bone-following without cloth; helmet/trouser baseline unchanged.

Prior torch body/model/icon and light work: TORCH_POLISH_20261113 and TORCH_WRAP_20261113. Their particle appearance is replaced by current volume. Environment materials/uneven floor/props/moon/stars/clouds retained: ENVIRONMENT_SURFACES_20261113, FIRE_FLOOR_20261113, ROOM_PROPS_SKY_20261113. Free/CC0 and original asset provenance in ASSET_PLAN; no Dark and Darker assets imported.

Physical six-pocket belt complete (three on3, three on4): potion/bandage cap5, torch1, actual bag custody, server moves/merges, profile schema6. Tasks/PHYSICAL_QUICK_BELT_20261113.md. Combined1391 EditMode cases after focused corrections, not fresh full aggregate. Relevant Hands/Loadout/NetInventory/NetExtraction/NetFinal/NetRoyale and graphics UI passed; isolated preparation restart preserved items.

Royale: three maps ruins→crypts→infernal; solo16, duo16, trio15, temporary destination bots; cross-session human matchmaking absent. Shrinking zone and three randomized party portal waves, last at final60s. Controlled floor custody/cleanup/extraction passed; ordinary full-match final evacuation/profile restart acceptance remains open (Tasks/SQUIRE_ROYALE_20261113.md).

## Next three actions

1. Follow user visual feedback on ground loot and torch; retain gallery/live evidence. New loot inventory icons, further armor variants/cloth animation remain separate scope.
2. Finish Royale ordinary full-match final evacuation/profile restart acceptance; controlled fixtures do not replace it.
3. Return to explicitly deferred native memory investigation when requested; preserve logs, no engine/package upgrades or unrelated drift cleanup.
