# Dungeon look: lighting and module furnishing — 25-26 November 2026

User request (25 Nov): work on the light and on the modules themselves — more variety and more content per module; build missing models from primitives (real models will be generated later); light should be dark but not eye-searing. Mid-task the user supplied the target: `C:/Users/yrapa/Videos/Captures/Dark and Darker   2026-11-25 23-39-37.mp4` ("this is how light and atmosphere should be").

Status: implemented, automated proof green, awaiting the user's in-game review — uncommitted.

## Reference evidence

- User recording (62 s, 2560x1072), frames every 2 s, HUD cropped: luminance mean 0.106, p50 0.054, p95 0.37, no clipping, crushed (<0.03) 44 %, warm pixels 38 %. Look: golden-amber fire, dense wall lanterns, braziers and candles, tight bright pools with hard shadows, cool violet-blue night between pools.
- Extraction `C:/Users/yrapa/Downloads/DarkAndDarker-extracted` (pakchunk602-Windows/.../Environment/Dungeon/Crypt/): 942 `SM_*` packages; prop families used as inspiration for the placeholders: `SM_BonesPile01..29`, `SM_BonesWall`, `SM_Sarcophagus01`, `SM_Cage01`, `SM_Chain*`, `SM_Banner01-03`, `SM_Candelabra_*`, `SM_Censer*`, `SM_GothicBench02`, `SM_Altar_01`, `SM_PropWeaponRack_B`, `SM_BookStack_01a`, `SM_Debris_Floor_*`, `SM_Column_Ruined_*`. Module packages (pakchunk2802-Windows/.../Maps/Dungeon/Modules/Crypt/): Vault_A lists banners, cages, censers, chains, `BP_LightBeam*`, `BP_FogSheet*`; Edge/Chapel lists `BP_Candle_A..A6`; Edge/SkeletonPit lists ~25 bone-pile variants and `BP_CandleWallOn*`. These are label names only: no transforms, sizes or light values were decoded.
- Limitation: `D:/DarkAndDarker-decoded` holds no level data (maps were not decoded), so no reference light intensities, fog or exposure values exist. All values below are our own tuning against the recording's measured look.

## Baseline defects found (before)

Mirror render `DungeonLookReview` (`Logs/review/dungeon-look-before`, 30 modules x 4 eye-height views, game FOV, raid scene volume): floor 1 mean 0.043, crushed 63 %; floors 2-3 crushed 82-85 %.
1. Every module had four `Brazier_Light` (intensity 22, range 18 m, colour FF7D30, no shadows; blue 4CA6FF on floor 2): a saturated orange flood through walls.
2. `MoonlitRuinsEnvironment` on the entry module had no moon assigned and disabled every directional light, so there was no moon at all.
3. Ambient fill never reached surfaces: the runtime colour change does not replace Unity's stored ambient probe (verified: a 5x probe boost changed nothing in Trilight mode; Custom mode works).
4. Grade: contrast 6 + warm colour filter (1, .93, .84) pushed fire to red-orange.

## Changes

- `Editor/DungeonLightPalette.cs` (new): one fire palette (golden amber (1,.66,.30), candle (1,.70,.36), ghost fire for floor 2) for braziers, layout lanterns, wall torches, candles, cache lamps and prop light sockets; braziers 12 / 12 m with wall-occluding soft shadows (shadow near plane past the bowl so it does not shadow its own floor). Menu `Emberdelve/Visual/Apply Dungeon Light Palette`. Builders (`CryptRoomArchitecture`, `DungeonLayoutLighting`, `DungeonRoomExpansion`, `EmberdelveTorchFactory`) now take their values from it.
- `Editor/DungeonLayoutLighting.cs`: perimeter lanterns (up to 10 per room; walls may be mesh colliders), lights `LightSocket` children of placeholder props, shared flame helper; sconce range limit 8.1 m.
- `Runtime/Level/MoonlitRuinsEnvironment.cs`: adopts the scene's `Moon_Slit_Directional` when none is assigned; cool violet fill set through `AmbientMode.Custom` + an explicit ambient probe; fog (.045,.05,.062) density .024; moon .45.
- `Editor/EmberdelveVisualSetup.cs` grade: contrast 4, saturation -8, colour filter (1,.98,.95) (postExposure 0.6, ACES, bloom, grain kept; Visual gate thresholds unchanged).
- `Editor/DungeonPrimitiveProps.cs` (new, Sonnet worker): 24 placeholder prefabs `Art/Generated/PrimitiveProps/PH_*` built from primitives (sarcophagi, coffins, bone piles, ossuary shelf, iron brazier, candle cluster, candelabrum, altar, pews, benches, weapon rack, cage, wall chains, banner, broken pillar, rubble, cot, cauldron, lectern, book pile, wall buttress) with 7 new materials; floor-centred pivots, no colliders, `LightSocket` on light props. Menu `Emberdelve/Visual/Build Primitive Placeholder Props`. Sheet: mirror `Logs/review/primitive-props/sheet.png`.
- `Editor/DungeonLayoutFurnishing.cs` (Sonnet worker, then Opus fix): per-kind identity — guard post, barracks, library, prison/armory, catacomb, sunken pit, chapel, treasury, ritual antechamber, ruin; Infernal adds braziers/cages/chains, floor-2 crypt adds coffins/skull heaps; buttresses, banners, chains and floor debris in every module; minimum 30 items per room (was 12).
- `Editor/DungeonLookReview.cs` (new): mirror batch review renderer with luminance stats.

## Evidence

- Lighting iterations v1-v6 in `Logs/review/dungeon-look-*` (stats.txt each). Final render after regeneration `Logs/review/dungeon-look-final`: floor 1 mean 0.064 (was 0.043) / crushed 40 % (63 %) / warm 9 %; floor 2 0.029 (0.020) / 68 % (82 %) / 3 %; floor 3 0.022 (0.015) / 78 % (85 %) / 6 %; no clipping. Recording: 0.106 / 44 % / 38 %. Our 36 m rooms seen from the centre show much more floor and sky than the recording's close quarters, and the review omits the player's hand torch (4.5 / 12 m), so the warm share is not directly comparable; floors 2-3 remain darker than the reference.
- First gate run `Logs/runs/20261126-001948-918`: Visual 216/0, NetDoor 49/0, NetMovement 7/2, NetFinal 23/2 (server: secret chest anchor disconnected), EditMode 1683/38 (furnishing broke side-chamber clearance, family routes, hall_03 reachability, secret cache, Royale party spawns). Escalation: Sonnet furnishing worker -> Opus worker (brief defect: the first brief did not name the navigation fixtures). The furnisher now reserves stair landings, family route corridors, chamber aisles and the lever approach, keeps colliding items on the ground floor, and bakes a hidden copy with the runtime nav baker/mask to check ports, centre, routes, chamber, lever, cache and >= 80 % of content cells (`NavContract`, repair loop drops blocking items). Worker run: the 7 fixtures 155/155.
- Second gate run `Logs/runs/20261126-011821-541`: NetMovement 198/0, NetFinal 172/0, NetDoor 49/0, Visual 216/0; EditMode 1683/10 — all 10 outside this slice (EnemyVisualQuaterniusTests 7, HandsRulesTests bone_bow, FirstPersonSwingTests, WeaponViewModelTests; combat/visual work of other sessions or the known baseline).
- Rooms hold 32-43 items (minimum 30), up to 10 wall lanterns and at most 8 light-socket props each.

## Open

- In-game review by the user (Play: flicker, hand torch, performance with ~20 shadowed point lights per room, motion).
- Floors 2-3 and the centres of the 36 m rooms are still darker/emptier than the reference: next steps would be ceiling/hanging lights (reference `SM_Candelabra_Hanging_*`, `BP_LightBeam`), central features in the reserved cross, fog sheets.
- Placeholder bone colour is warm and the bone pile has a dark base disc.
- No Play-mode review yet (flicker, hand torch, motion); the user's in-game check decides acceptance.
