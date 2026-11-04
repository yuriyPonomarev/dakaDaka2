# V3b - grim undead enemies (Quaternius Humanoid mannequin)

Request: the cartoony KayKit skeletons are replaced by a grim, less cartoony look (CC0 Quaternius Universal Animation Library 1 + 2, Humanoid), same Animator contract, no gameplay change.

## Done
- `EmberdelveQuaterniusImport` (Editor, `AssetPostprocessor` + `EnsureConfigured`, menu Emberdelve/Visual/Configure Quaternius Assets): the three files under `Art/ThirdParty/Quaternius/` (UAL1 `AnimationLibrary_Unity_Standard.fbx`, UAL2 `UAL2_Standard.fbx`, `Mannequin_F.fbx`) are Humanoid with the avatar of their own model, no cameras/lights/colliders/materials, mesh readable, bones kept; per clip: idle/walk/run/jog/sprint/loop/shamble loop, root rotation and XZ position baked into the pose (`applyRootMotion` is off anyway). Idempotent (reimports only on change). `manifest.json` (source URL, creator, CC0-1.0, SHA256 + size per file) and `LICENSE_CC0.txt` are written by the same class; the SHA256 values are the ones of the downloads.
- `EmberdelveUndeadMaterials`: the mannequin has no texture (UAL1 mesh has no UVs at all), so a derived mesh per mannequin (`Art/Models/Undead/Undead_Mannequin_{M,F}.asset`: same skin weights / bind poses, one submesh, planar UVs u = lateral X, v = height) and one body texture + URP Lit material per variant (`Art/Materials/Undead/Undead_<Variant>_Body.{png,mat}`: 512 px, Point, deterministic) with the clothes painted by position (ragged hems, tears showing rotten flesh). Flesh tints: Minion grey-green (0.30,0.33,0.27 base), Warrior ash, Rogue blue-grey, Mage pale ash. `Undead_RustyMetal.mat` (weapons, shield, pauldrons) and `Undead_EyeGlow.mat` (emissive ember, URP Lit, HDR 1.8).
- `EmberdelveEnemyVisualFactory` rewritten (now `public`): per variant AnimatorController (Idle/Walk/Run/Attack/Hit/Death; Speed, AttackSpeed, Attack, Hit, Dead; same transitions as V2) and prefab `EnemyVisual_{Minion,Warrior,Rogue,Mage}` = Model (mannequin rotated 180 deg because the FBX mannequin faces -Z, Animator + Humanoid avatar, derived mesh, variant material, fitted to NormalHeight 1.9), weapons on the Humanoid hand bones, `EnemyVisualDriver` unchanged. `Attach()`, `VariantFor`, `NormalHeight`, `EliteScale` unchanged. The attack clip is copied to `Prefabs/Visual/EnemyVisual_<V>_Attack.anim` named `Attack` because the driver finds the clip length by that name.
  - Minion: male, zombie idle/shamble (run = same clip at 1.7x), Sword_Attack, rusty blade. Rogue: female (`Mannequin_F`), Idle/Walk/Jog, Sword_Regular_C, rusty dagger (blade scaled down). Warrior (elite): Idle_Shield, Walk_Formal, Jog, Sword_Attack, rusty axe + shield on the hands, rusty pauldrons. Mage: Spell_Simple_Idle / Spell_Simple_Shoot, long robe, two emissive eye spheres on the head bone (no collider); no enemy maps to it yet. Hit = Hit_Chest / Hit_Head, Death = Death01.
  - The grip of weapon and shield is derived from the poses meant for them (blade in `Sword_Idle`, shield in `Idle_Shield_Loop`), not hard-coded bone offsets.
- Removed: every dependency of the factory on KayKit skeleton characters (the KayKit files stay on disk; the blade / axe / small shield meshes are placeholders re-skinned with the rusty material).
- `EmberdelveEnemyPreview` (Editor tool): renders the four prefabs side by side, posed by a clip, to a PNG without touching the open scenes.
- Scenes: `EmberdelveBootstrapMenu.BuildEverything` (and Level01 reopened). Enemy gameplay colliders, hit volumes, timings, EnemyAgent/EnemyBrain and networking are untouched.
- Gate: `EmberdelveRetroVisualGate` now also checks the Quaternius manifest/hash/Humanoid import (`CheckQuaternius`), a Humanoid avatar on every enemy visual, and eyes on the head bone exactly where the variant asks for them.
- Tests: new assembly `Assets/Emberdelve/Tests/EditMode/Visual/` (`Emberdelve.Visual.Tests.EditMode`, `EnemyVisualQuaterniusTests`, 45 cases): manifest hash/size/license, Humanoid avatars, loop flags and baked root from the importer's own loop pattern, every variant's clips resolve, prefab has driver + Humanoid animator + no colliders, controller = 6 states / 5 parameters / non-null motions / looping locomotion / attack speed parameter / an `Attack` clip, height within 8 % of NormalHeight, materials URP Lit from the undead folder and no KayKit atlas, no dependency on a KayKit skeleton character, weapons on the hand bones with rusty metal, eyes exactly on the variants flagged (and only the Mage), flesh textures dark / desaturated / different per variant, derived mesh keeps skinning, `VariantFor` from the EnemyData assets.

## Checks actually run (open Editor)
- `console_get level=error`: empty after the final compile.
- EditMode assembly filter `Emberdelve`: 600 total, 600 passed (555 before + 45 new).
- `EmberdelveGateSuite.StartInteractive("fast")`: 12/12 PASS (`Logs/quick/v3b/summary.txt`).
- RetroVisual gate: cannot run in the open Editor (batch + Exit). `CheckQuaternius` + `CheckEnemies` by reflection on the rebuilt Level01: 6 checks, 0 failures. The rest (KayKit import, lobby rebuild, textures, props) is Run-All.

## Evidence (`Logs/quick/v3b/`)
`v3b_variants_idle.png`, `_walk.png`, `_attack.png`, `_death.png` (Minion, Rogue, Warrior, Mage left to right, preview scene, retro pass off), `v3b_mage_head.png` (glowing eyes), `v3b_level_*.png` (Level01 in edit mode: bind pose, the enemies animate only in Play).

## Not done / risks
- Dark: albedos follow the brief (dark, desaturated), flesh lifted x1.25; in dim corners of the dungeon the silhouettes are low-contrast. Easy to tune in `EmberdelveUndeadMaterials.Shade`.
- The mannequin head is a plain mask (no face), and the UV bake is planar: front and back of the body share the texture, the sides are smeared.
- Weapons are KayKit meshes (chunky) re-skinned; real textured weapon models belong to the weapon-visual slice.
- Attack clip choice vs the brain's timing: the driver matches the clip speed to windup + 0.5 s as before (clamped 0.6-2.2x); short library clips (Spell_Simple_Shoot 0.5 s) play at the clamp. Only looked at still frames, not played in a build. Rogue uses `Sword_Regular_C` (2.0 s) as a stab substitute: the library has no dagger stab.
- Eyes use a plain HDR emission (no bloom in the project), so they read as orange-yellow discs.
- Run-All (mirror) not run.
