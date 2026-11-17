# Cinder Warden in the crypt

The existing elite slot in the primary Modular Crypts raid now contains the Cinder Warden. The legacy Ashen Warden asset remains available for its own encounters and combat fixtures. The authored fallback spawn is cell `(7, 17)` in the Crypt room. The modular assembler carries the Cinder Warden actor to the spawn reserved with its larger dimensions.

The boss starts awake with 320 HP and uses the existing enemy perception, navigation, damage, death and loot systems. It patrols a short area and chases a detected player. The root turns toward the committed direction of the overhead swing, and the raid authority owns all hit decisions. The club uses 13 sampled positions from the Meshy animation between normalized times 0.83 and 0.90. The damaging segment covers the ironbound head with a 0.27 m radius. The authority checks that segment for 0.22 s after the windup and applies a base 39 damage before the standard body-part and defense rules. The source swing and the death animation are recorded in [ASSET_PLAN](../ASSET_PLAN.md).

The local enemy driver and network proxy use the same active animation interval. The new visual kind uses protocol 42. The regenerated walking hit volumes and the saved body and head colliders allow the player to damage the larger enemy.

## Verification

- `EnemySweep` passed 121 checks in Unity 6000.2.5f1 in `Logs/runs/20261117-215251-097`. The dedicated boss flow passed 13 checks covering its starting HP, animator, club pattern, head volume, targeting, telegraph, single active hit, player behind the swing, incoming damage and death.
- The same run passed `NetCombat` with 397 checks and `NetFinal` with 172 checks. The lobby-to-raid proof used generated layout seed `-628729479` and hash `06756413`.
- `NetEnemyPresentationWireTests` passed 22 checks in the primary Editor.
- `MovementProofGeometryTests` passed all five checks. `AuthoredFloorStagesWithoutDirectorOrMutatingCurrentMap` passed six generated layouts using the changed Ashen Halls content asset.
- `CinderCrypt` passed eight checks in `Logs/runs/20261117-220207-108`. The saved Modular Crypts scene contained one active 320 HP Cinder Warden. Its actual club attack took the scene player from 120 to 61.5 HP after the warning, with no windup damage.
- The same run passed `Visual` with 216 checks, `NetMovement` with 198, `NetDoor` with 37, and `NetInventory` with 148.
- The harness proposed a full suite because the imported model and shared network additions are outside its narrow rules. Verification is scoped to the creature's combat, affected enemy presentation and raid startup. The inherited Meshy warrior changes remain a separate review.
