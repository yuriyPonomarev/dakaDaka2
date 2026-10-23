# Projectile weapon blocking

## Behavior

Ordinary bow arrows, crossbow bolts, MultiShot arrows and PiercingShot arrows are authority-owned flights. The authority validates the active ranged weapon, authoritative aim and origin, positive range, sequence/cooldown, ammunition, skill activation and canonical spread slot at launch. It advances flights using authored projectile speed, sweeps the visible shaft half-width against walls, body shapes and the live guarding blade, and applies damage only at impact. The cosmetic arrow follows the same authority position with its leading tip aligned to the simulated projectile.

Sword and rapier interception uses one authored blade pose and volume shared by the first-person weapon visual and authority. Blocking requires the moving projectile path to intersect that blade before body contact. The blade pose is resolved from authoritative aim, current guard and equipped weapon state, without requiring a rendered object on the authority. Raising, lowering or moving guard during flight changes the result. A blade near miss still damages the body; rear shots and non-guarding, swinging, dead, swapped or non-guardable defenders do not receive blade interception.

Shield behavior remains on its existing directional mitigation path and shields still cannot parry. No parry timing window was added. The rapier has a data-authored 0.7 block reduction override, matching the existing Warrior base mitigation; the primary weapon override applies when no contributing shield takes precedence. Equipping an Archer rapier enables its guard without enabling bow blocking. Guardbreak behavior is retained on actual blade contact.

Ranged hit-only `SubmitAttack` and `SubmitSkill` requests cannot apply immediate projectile damage. A valid launch snapshots its damage modifiers, spread and origin as appropriate; an already launched flight resolves after the skill activation closes or equipment changes. Damage deduplication is separate from collision, so volley/piercing arrows still stop on bodies they already damaged. Fast flights are swept by segment, with nearest wall/body/blade ordering; saturated physics queries use a complete-query fallback. Launch is rejected if the initial projectile overlaps level geometry or the eye-to-muzzle path is obstructed. Entities on inactive GameObjects are not analytic ghost blockers. Armor mitigation preserves projectile and blade-contact provenance.

## Task-specific changed files

- Runtime combat: `Assets/Emberdelve/Runtime/Combat/LocalCombatAuthority.cs`, new `LocalCombatAuthority.Projectiles.cs`, `CombatContracts.cs`, `DamageAndScanning.cs`, `ProjectileVisual.cs`, `RangedCombat.cs`.
- Player, damage contracts and data: `Assets/Emberdelve/Runtime/Player/PlayerRig.cs`, `WeaponVisualRig.cs`, `ClassLoadout.cs`, new `GuardBladeBox.cs`; `Assets/Emberdelve/Runtime/Core/GameContracts.cs`; `Assets/Emberdelve/Runtime/Data/DefenseModel.cs`; `Assets/Emberdelve/Data/Weapon_EmberRapier.asset`.
- Ranged skills and authoring: `Assets/Emberdelve/Runtime/Skills/RangedSkillUtility.cs`, `Assets/Emberdelve/Runtime/Skills/Behaviors/MultiShotBehavior.cs`, `Assets/Emberdelve/Editor/EmberdelveAssetLibrary.cs`.
- Gates: new `Assets/Emberdelve/Editor/EmberdelveProjectileBlockGate.cs`, plus `EmberdelveCombatAuthorityGate.cs`, `EmberdelveCombatFlowGate.cs`, `EmberdelveSkillAuthorityGate.cs`, `EmberdelveAttributeSkillGate.cs`, `EmberdelveRuntimeSmokeTest.cs`, and `EmberdelveFlatDamageAffixGate.cs`.

`SkillContracts.cs` and `PiercingShotBehavior.cs` were inspected and have no task delta; they are excluded from this task-specific list. Other unrelated dirty files were preserved and not included.

## Verification

The root runner executed Unity gates; the implementer did not run Unity or tests.

| Gate | Result |
|---|---:|
| ProjectileBlock focused authority/geometry | 50 checks, 0 failures (Run14) |
| CombatAuthority ranged authority regressions | 49 checks, 0 failures (Run12) |
| SkillAuthority ranged/skill regressions | 161 checks, 0 failures (Run12) |
| Archer runtime smoke | 120 checks, 0 failures (Run11) |
| Swept melee regression | 638 checks, 0 failures (Run13) |
| CombatFlow regression | 376 checks, 0 failures (Run13) |
| Full AttributeSkill gate | 925 checks, 0 failures (Run14) |
| Live rendered capture | Run17 exited 0; 39 live frames advanced, guard remained active, tip was 0.573 m from the blade, and visible/authority blade poses matched. The root inspected the PNG and confirmed the HUD, raised blade and incoming arrow are visible. |

The flat weapon-damage numerical gate suspends active registered `EnemyAgent` scene objects for its standalone fixture, stores their active states, and restores them in `finally`. It also asserts that no active registered non-fixture body intersects the 32 m ranged test lane. Projectile collision against the fixture target remains active. A diagnostic run found the earlier mismatch was a valid prior impact: `Enemy_Hollow Servitor_2` was hit at about 15.09 m for 48.8992 damage, before the synthetic target at 32 m. The numerical oracle and `.001` tolerances are unchanged; temporary ray and spherecast logging has been removed. The isolated gate needs a rerun.

The optional `EmberdelveProjectileBlockGate.RunCapture` keeps the real MainCamera and HUD active, launches an authority-driven diagnostic arrow, waits for the live arrow tip to approach the guard blade, and saves [ProjectileBlock_LiveDiagnostic.png](C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka/Logs/ProjectileBlock_LiveDiagnostic.png). The gate slows a cloned bow only for this diagnostic so the incoming arrow can be captured between frames. Run17 was executed in the normal Editor after the graphics batch capture timed out; it advanced 39 live frames before capturing with the guard active and blade-pose assertion passing. This is a diagnostic view, not a manual PvP or network test.

## Preservation and limits

The project already contained many dirty Opus changes. They were preserved without reset, cleanup, staging or commit. Exact pre-edit bytes for pre-existing dirty files were saved under `C:\Users\yrapa\.pi\agent\projects\unity-emberdelve\review-baseline-projectile-block`. Additional implementation checkpoints and intermediate copies are in separate sibling directories under `C:\Users\yrapa\.pi\agent\projects\unity-emberdelve`. `LocalCombatAuthority.Projectiles.cs` was new for this task, so it has no pre-task baseline. During implementation, its ordinary validation and skill launch methods were reconstructed after an overbroad replacement; the intermediate bytes were retained under `current-before-canonical-repair`, but are not represented as pristine source. The AttributeSkill gate was edited before an original pre-task snapshot was taken; `current-before-more-edits` is an intermediate copy, not pristine pre-task bytes. Subsequent exact checkpoints and Unity gates cover the repaired current code.

This uses `LocalCombatAuthority` as the current offline authority. The gates do not establish network server ownership, remote replication or online lag compensation.
