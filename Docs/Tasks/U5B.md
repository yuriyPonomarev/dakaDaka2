# U5B: arming sword and two-handed sword

Two new melee weapons, equippable like every other weapon (hand slots, drop/pickup, catalog item, chest loot). All numbers are data in
`EmberdelveAssetLibrary` (`ApplySwordFamily`), nothing is typed into runtime code. Server authority, damage resolution, hit volumes,
swing timing code and net messages are untouched; the new weapons only add data, patterns, items and view models.

## Data and balance

| | ashen_short_sword | arming_sword | two_handed_sword | warden_maul |
|---|---|---|---|---|
| hands | one | one | two | one |
| base damage | 26 | 28 | 40 | 26 (x pattern) |
| windup / active / recovery (s) | .24 / .30 / .30 | .26 / .30 / .32 | .42 / .34 / .50 | .85 / .18 / .50 |
| cooldown (s) | .92 | .98 | 1.45 | 2.5 |
| reach / arc / hit sphere | 2.3 / 120 / .6 | 2.45 / 120 / .6 | 2.9 / 150 / .65 | 2.5 / 160 / .7 |
| impactPower | 10 | 11 | 12 | 30 |
| impactResist (own guard) | 15 | 16 | 22 | 15 |
| held move speed penalty | 10 | 11 | 20 | 0 |
| swing patterns | 3 | 4 | 4 | 1 |
| riposte | yes, with shield | yes, with shield | yes, no shield | no |

How the numbers were chosen:

- arming sword: the plain sword. A touch longer (2.45 vs 2.3) and a touch heavier (28 vs 26 damage, windup +.02, recovery +.02), so the
  swing rate is the standard sword rate (about 1 hit per second). It reuses the short sword's left-right and right-left slashes and the
  overhead, and adds its own thrust (reach scale .75 of the weapon, a short fast jab) as a combo step. Combo: LR, RL (x1.05), thrust
  (x1.10, slightly faster), overhead (x1.15 finisher). Riposte = thrust then RL, x1.5, shield allowed (data flag).
- two_handed_sword: the heavy sword. 40 damage (about 1.5x the sword), windup .42 and recovery .50, so a swing takes about 1.45 s: its damage per
  second is about 15 percent above the arming sword, paid for with a long vulnerable window and a speed penalty of 20 while held. Wide
  left-right and right-left arcs (150 degrees, up to 3 targets), overhead, thrust. It cannot carry a shield, so its own guard is better:
  impactResist 22 (the arming sword 16).
- impactPower 12. The brief asked for a value "between sword impactResist 15 and maul 30 so it does NOT break a sword guard"; these
  contradict each other, because a guard breaks when power x pattern scale exceeds resist, so any power between 15 and 30 breaks a
  resist-15 guard. Decision: the designed OUTCOME wins. The two-handed sword does not break a one-handed sword guard, so impactPower is 12
  (the overhead scale 1.2 gives 14.4, still below 15 and below the arming sword's 16), higher than the short sword (10) and arming sword (11),
  far below the maul (30). Breaking guards stays the maul's job. A test proves "does not break a sword guard" for every pattern from the
  data. If design later wants it to break a sword guard, raise impactPower above 15 in `ApplySwordFamily`; no code change.

## Catalog and loot

- items `arming_sword` (value 50, 1x3 grid, Uncommon) and `two_handed_sword` (value 95, 1x4 grid, Rare), equip slot Weapon, granting the
  weapon. The item id is the weapon id.
- loot: arming sword weight 2 in the ember chest table, two-handed sword weight 2 in the sealed chest table (one copy each).
- starting choice: there is no kit-choice mechanism (classes carry one primary weapon), so the swords are loot only.

## Models (CC0)

Poly Haven has no usable longsword (its swords are the estoc already used). Quaternius Modular Weapons (Claymore, Sword) is CC0 but its Google
Drive download returned a quota error from the PC, so it could not be fetched (a retry later is the best upgrade path). OpenGameArt CC0 models
were used (author, source, SHA256 in `Assets/Emberdelve/Art/ThirdParty/OpenGameArt/manifest.json` and `Docs/ASSET_PLAN.md`):

- arming_sword: Knight's Sword by Teh_Bucket (OBJ + PNG), model length 0.95 m.
- two_handed_sword: One Handed Sword by LordNeo (FBX), long slender blade, normalised to 1.25 m. It is a one-handed model with a short
  grip, used for the two-handed sword because it is the only realistic long blade found; the second hand sits at the pommel end.
- rejected: Great Sword by Mikeask (toy-like, no crossguard); stretching the estoc (not acceptable).

Both go through the existing normalisation (grip at the origin, tip +Y, URP/Lit materials with `_BaseMap`, no colliders) and the weapon view
model set.

## First person

Swings are unchanged: the right hand follows the real SwingPatternData trajectory of the new patterns. For a two-handed weapon the left hand
is solved on a second grip: `FirstPersonArmsDriver.SecondGripOffset` (0.1 m at model scale) behind the right grip along the model's own axis
(before: along the grip transform, which put it out of frame). No shield is shown while a two-handed weapon is held. The arming sword is held
like the short sword. Enemies are not affected.

## Tests

- `SwordFamilyTests` (new): hands, riposte flags and strikes, pattern count and validity, balance relations read from the sibling weapons'
  data, the two-handed sword does not break a one-handed sword guard while its guard resists more, catalog items grant the weapon, both can
  drop from loot, a two-handed weapon gets no shield contribution from the guard profile.
- `RiposteDataTests`: the "only the short sword" lists now include the sword family (flags read from data).
- `FirstPersonSwingTests`: the new patterns are covered automatically (data driven); new test for the left hand on the second grip.
- `WeaponViewModelTests`: every weapon maps to a model; both new models are normalised, URP/Lit, no collider.

Evidence: `Logs/quick/u5b` (lineup.png, sheet_arming_sword.png, sheet_two_handed_sword.png: idle, left-right, overhead, thrust, block).

## Compromises

- The OGA models are low poly and stylistically different from the Poly Haven set; the long sword has a large ring pommel that dominates the
  first-person view close to the camera and hides the left fist.
- No starting-kit choice (no mechanism), loot only.
- The feel in Play mode (two-hander timing, speed penalty 20) is not verified by a person.
