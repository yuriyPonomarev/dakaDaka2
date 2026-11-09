# Network inventory fixture clearance — 8 November 2026

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


The full run `Logs/runs/20261108-042911` failed NetInventory 42/4. Its contest fixture initially placed B at (36.650, 0.150, 11.350); after movement physics the client was at (37.120, 0.030, 11.350), with `Hall_CentralPillar` blocking the chest ray. The server and client had matching modular plan hash `67543D13`. The fixture now rejects positions where the grounded CharacterController capsule overlaps level geometry, while retaining the production range and line-of-sight checks on both server and clients. The floor-loot fixture uses the same clearance test.

For floor loot, candidate positions are also checked with `ValidateNetworkInteraction`, the actual network home-position geometry. The prior generic interaction check could select a position whose ray failed once the pickup had a network ID. The separate occlusion rejection remains in the test.

Focused run `Logs/runs/20261108-045120` passed NetInventory 146/0, including client reach, contested exact UID, blocked LOS, replay and dead-player rejection. This single random layout does not establish clearance across every possible seed. The full run above predates these changes and remains failed.

Subsequent seed `3954420229` (map hash B8BFA034) exposed a separate corpse fixture failure in `Logs/runs/20261108-045433`: A's near request returned `No line of sight`. Seed `1542870691` (hash 67543D13) then exposed a corpse location with no clear B approach and a host position that lost LOS after physics settled. The fixture now considers at most 24 candidate corpse directions and registers the corpse only after both players have distinct, free, production-valid approaches. Near staging checks controller clearance and server LOS; the deliberate blocked-LOS check is retained and its blocker is deactivated before destruction. Host staging uses the same clearance check.

On the final version, fixed seed `1542870691` passed NetInventory 146/0 in `Logs/runs/20261108-050856`, and `3954420229` passed 146/0 in `Logs/runs/20261108-051115`. These two seeds reproduce the observed failures; they are not exhaustive coverage of every generated map.

The same focused run passed DrawSpeed 203/0. Its gate now checks `Bow_String` and `Bow_Arrow` against the revised 0.52 bow string, and `Crossbow_String` and `Crossbow_Bolt` against the 0.43 horizontal string, retaining position and draw-progress checks at four fractions. The earlier full run's 191/8 reflected the superseded bow dimensions and missing crossbow-specific names.
