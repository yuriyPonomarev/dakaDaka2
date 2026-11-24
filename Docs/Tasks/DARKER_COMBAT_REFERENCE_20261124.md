# Dark and Darker combat reference (decoded) - 24 Nov 2026

User request (24 Nov): finish decoding the local Dark and Darker extraction and make our combat match it
(animation timings, parry, bow and arrows). This file holds the DECODED FACTS. Our adaptation is in
[DARKER_COMBAT_PORT_20261124](DARKER_COMBAT_PORT_20261124.md).

## How the data was decoded

The extraction `C:/Users/yrapa/Downloads/DarkAndDarker-extracted` is 123k Zen packages (`.uheader` + `.uexp`) whose
properties are "unversioned": no names, only an index into the class's property list. That list lives in the game
executable's static reflection tables. `Tools/DarkerReference` reads them (the executable is only read, never run):

- `reflect_db.py <DungeonCrawler.exe> <schema.json>`: classes, structs, enums with ordered properties (a `.usmap` equivalent).
- `zen_decode.py <schemas> <out> <extracted root> <globs>`: packages -> JSON. Each export is accepted only when a
  schema consumes its bytes exactly.
- `montage_summary.py`, `weapon_sheet.py`: notify timelines and one-screen weapon sheets from the JSON.
- `cityhash.py`, `pe.py`: helpers (script-object ids, PE reader). `python cityhash.py` self-checks against package values.

Two builds are installed and NEITHER is the build of the extraction (it lies between them), so both schemas are given
and the decoder picks per export: `C:/DarkAndDarkerTest/...` 0.17.153 (5 Nov) and Steam 0.18.161 (19 Nov).
Output (not in the repo, game-derived): `D:/DarkAndDarker-decoded` (schemas + JSON mirror of the package paths).

Coverage: design data `Data/Generated/V2` 28,446 of 28,733 packages decode exactly (175 of 183 tables; the failing
ones are skins, dungeon lists, merchants, leaderboards, PlayerCharacter, PropsInteract). Weapon montages, ability
default objects and curves decode. NOT decoded: Blueprint graphs and native code (the logic), Blueprint-added
variables, animation curves/bone tracks, and defaults set in C++ constructors (they are not stored in packages).

Rebuild: see `Tools/DarkerReference/README.md`.

## Time base

Montage times are seconds at play rate 1. The game multiplies play rate by action speed. `hit a-b` is the window
between `Event.Attack.Collision.On` and `.Off`; `transition` is `Event.Attack.Transition` (a queued next attack
starts here); `comboClose` is `Event.Attack.ComboWindow.Close`; `guardReady` is `Event.Item.ImpactGuard.Ready`.
`PlayerMeleeAttackComboInputQueueTime` = 0.2 s.

## One-handed and two-handed swords (source: `Item/Weapon/<Name>/Animations/AM_*`, `Data/Generated/V2/...`)

| Weapon | Attack | Length | Hit window | Transition | Combo close | Damage ratio | Type |
|---|---|---:|---|---:|---:|---:|---|
| ArmingSword | 1 | 1.800 | 0.399-0.603 | 0.837 | 1.130 | 1.00 | slash |
| | 2 | 1.600 | 0.400-0.557 | 0.933 | 1.260 | 1.05 | slash |
| | 3 | 1.967 | 0.600-0.754 | - | - | 1.10 | pierce |
| | riposte | 1.600 | 0.431-0.636 | - | - | x1.5 parry | slash |
| ShortSword | 1 | 1.400 | 0.513-0.633 | 0.733 | 0.990 | 1.00 | |
| | 2 | 1.467 | 0.500-0.633 | 0.833 | 1.125 | 1.05 | |
| | 3 | 1.600 | 0.615-0.787 | - | - | 1.10 | |
| | riposte 1 / 2 | 1.367 / 1.250 | 0.385-0.503 / 0.382-0.561 | 0.634 | 0.856 | x1.5 parry | |
| Rapier | 1 | 1.067 | 0.433-0.502 | 0.614 | 0.829 | 1.00 | |
| | 2 | 1.400 | 0.669-0.748 | 0.928 | 1.140 | 1.05 | |
| | 3 | 1.500 | 0.651-0.731 | 0.971 | 1.240 | 1.10 | |
| | 4 | 1.367 | 0.596-0.679 | - | - | 1.15 | |
| | riposte 1 / 2 | 1.300 / 1.133 | 0.458-0.624 / 0.355-0.488 | 0.704 | 0.950 | x1.5 parry | |
| Longsword (2H) | 1 | 2.167 | 0.932-1.161 | 1.444 | 1.894 | 1.00 | |
| | 2 | 1.867 | 0.679-0.913 | 0.967 | 1.305 | 1.05 | |
| | 3 | 1.967 | 0.757-0.990 | - | - | 1.10 | |
| | riposte 1 / 2 | 1.533 / 1.533 | 0.333-0.500 / 0.433-0.567 | 0.833 | 1.125 | x1.5 parry | |
| Zweihander (2H) | 1 | 2.200 | 0.929-1.172 | 1.506 | 1.940 | 1.00 | |
| | 2 | 2.133 | 0.795-1.033 | 1.600 | 1.873 | 1.05 | |
| | 3 | 2.167 | 0.967-1.200 | - | - | 1.10 | |
| | riposte | 2.233 | 0.765-0.881 | - | - | x1.5 parry | |

Other attack facts:
- Arming sword blends: attack 1 blend in 0.2, all blend out 0.3; attacks 2-3 have no blend in.
- "Block allowed again" (`Event.Input.SecondaryAttack.Enable`): arming sword 0.775 / 0.867 / 1.067.
- Second target of one swing does 90 % (`AttackHit2 ExecDamageWeaponRatio 900` vs 1000).
- Hit-stop (attacker's montage play rate on a hit): 0.08 for 0.22 s (arming sword 2nd: 0.06 for 0.25; short sword
  0.05 s duration; zweihander 0.09).
- Hitting a wall: play rate -0.1 for 0.1 s (the blade bounces back), blend out 0.6 s. Shield contact: 0.3 s.
- Move speed during an attack: arming sword x0.875 before the hit window, x0.75 inside it; short sword 0.95 / 0.90;
  rapier 0.875 / 0.75; zweihander 0.825 / 0.65; riposte x0.85; "stuck" x0.6.
- Hit slow on the target (`HitSlow`): move speed -100 for 1 s (a Blueprint effect class scales it; curve not decoded).
- Weapon damage (uncommon, 3001) and held move speed: arming sword 32-34 / -20; short sword 25-27 / -10;
  rapier 20-21 / -17; longsword 40-42 / -40; zweihander 46-49 / -50 (base move speed 300, cap 500).
- Impact power / own impact resistance: arming sword 3 / 3; short sword 2 / 3; rapier 2 / 3; longsword 4 / 3;
  zweihander 4 / 4; every riposte hit 4 (rapier 2); recurve and longbow arrows 3; crossbow bolt 4.

## Guard, block reactions, parry

- Guard montage (every weapon): `Default` 0.2 s -> `PreLoop` 0.2 s -> `Loop`; `guardReady` at 0.150 s after the press;
  release plays `End` 0.4 s (0.6 s clip at x1.5). Longsword / zweihander have a separate `Block` section.
- While guarding move speed x0.75 (bows, crossbow, zweihander x0.70). Defending hitbox multiplier 0.9.
- Block reaction by the strength of the blocked hit: weak 0.45 s, mid 0.90 s (1.0 s clip at x1.11), heavy 1.30 s
  (1.5 s at x1.15); move x0.97 (zweihander x0.85).
- Parry: `MontageToPlayOnParrying` = `AM_<Weapon>_Deflect`, 1.0 s. The attacker plays `AM_<Weapon>_Deflected`,
  1.267 s (longsword / zweihander 1.133 s).
- Riposte: `ParryRiposteDamageRatio` 1.5; riposte attack 1 chains into attack 3 of the normal combo (short sword,
  rapier, longsword: `SetNextAttack ... Attack03`); a second riposte attack exists for short sword, rapier, longsword.
- Not decoded: the parry timing window, the weak/mid/heavy thresholds (impact power vs resistance), stamina rules.
  They are native code.

## Bows and crossbow

| | Recurve bow | Longbow | Crossbow |
|---|---:|---:|---:|
| Draw (start section) | 1.500 s | 2.000 s | - (fires 0.067 s after the press) |
| Draw with Nimble Hands | 1.267 s | 1.700 s | - |
| Hold loop | 1.600 s | 1.460 s | - |
| Release: `FireProjectile` after `End` begins | 0.014 s | 0.052 s | - |
| Recovery (`End`) | 1.167 s | 0.933 s | 0.800 s total |
| Reload montage / `Reload` event | 2.967 / 2.500 s | 2.967 / 2.500 s | 3.460 / 2.673 s |
| Arrows before reload (`MaxAmmoCount`) | 8 | 5 | 1 |
| Move speed drawing / guarding / reloading | x0.65 / x0.70 / x0.50 | - / x0.70 / x0.40 | x0.65 / x0.70 / x0.30 |
| Weapon damage (uncommon) | 35-37, armor pen 5 % | 43-46, 10 % | 52-55, 30 % |
| Impact power | 3 | 3 | 4 |

- Arrow (`Projectile/Arrow/BP_ArrowProjectile`, `LongbowArrow/BP_LongbowArrowProjectile`): `ProjectileGravityScale`
  0.98. Longbow arrow `InitialSpeed` 3250 cm/s (32.5 m/s). The recurve arrow does not override `InitialSpeed`
  (the base value is a native default, NOT decoded).
- Speed curve over flight time (`Data/GameplayAbility/CurveFloat/CV_RecurveBow`, used as the projectile's
  `AccelerationCurve`; cubic keys): 1.0 until 0.05 s, 0.8 at 0.18 s, 0.3 at 4.0 s. Longbow: 1.0 until 0.05 s,
  0.825 at 0.17 s, 0.025 at 4.0 s. Reading these as speed multipliers is an INFERENCE from the property name and
  the companion `CV_FallingDebris_Velocity`; the code that applies the curve is native.
- Bows can guard (`AM_RecurveBow_Guard`, `guardReady` 0.150 s) and have weak/mid/heavy block reactions.
- Not decoded: how draw fraction maps to damage and speed (ability graph), damage falloff by distance.

## Body part multipliers and misc constants (`Data/Generated/V2/Constant`)

Head 1.5, body 1.0, arm 0.8, hand 0.7, leg 0.6, foot 0.5; max headshot damage modifier 2.0; base move speed 300,
max 500; projectiles in water: velocity x0.17, gravity x0.33.

## Skeleton enemies (previous notes)

See SKELETON_OVERHEAD_20261124 and SKELETON_ARCHER_20261124; their "not decoded" timing notes can now be resolved with
`weapon_sheet.py` / `montage_summary.py` on `Characters/Monster/Skeleton/...` (chunk 301, not decoded in this pass).
