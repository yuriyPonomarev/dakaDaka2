# Archer spread geometry smoke fixture — 2026-10-24

## Scope

External draft only. The only code delta is in `Assets/Emberdelve/Editor/EmberdelveRuntimeSmokeTest.cs`; its `.meta` file is byte-identical to the captured before copy. No runtime, scene, collider, tolerance, damage formula, or expected-three-hit assertion changes are included.

## Failure diagnosis

The previous fixture selected a clear lane, then placed A/B/C at lateral offsets computed from the player transform and the four-metre target plane. The authoritative MultiShot path launches every projectile from the settled rig muzzle and rotates the settled aim by the authored spread angle. The R10d log showed player `(7, 0.08, 5)`, muzzle `(7.16, 1.46, 5.77)`, and aim `(0, -0.12, 0.99)` for a 26-degree spread. At the z=9 plane, the left ray is approximately x=6.41 while the prior C center was x=6.08, a roughly 0.33m lateral mismatch before body/collider shape is considered. The prior corridor check tested a lane envelope from the player origin, rather than the three actual diverging rays.

The prior volley log recorded two actual damage recipients: `Enemy_Ashen Warden_4` and `Enemy_Hollow Servitor_0`, each with expected per-arrow damage. Based on end-of-test target parking order and scene CombatId allocation, the missed slot is likely C (`Enemy_Hollow Servitor_5`, CombatId #1); that mapping is inferred because the prior callback log did not include A/B/C slot labels.

## Draft change

After `AimAtPoint` and the existing settling frames, the fixture now captures the actual muzzle and normalized aim. It independently computes the three authored spread rays using `Quaternion.AngleAxis` and the skill's projectile count/spread, intersects each against the existing forward target plane, and moves the corresponding target root to the ray's horizontal point while preserving its floor height. It preserves target colliders and rotates targets toward the shooter. Physics transforms are synchronized, then a same-frame sphere-cast preflight uses the configured hit/level masks and the bow's actual projectile radius to record the first body or level contact per ray and assert that each ray reaches its intended target first.

Diagnostics record each intended target's name, CombatId, position, collider dimensions, ray origin/direction, first contact, and later actual damage recipient/amount/point. The original weapon/mana checks, per-recipient damage formula, and `hitTargets == 3` assertion remain intact. There is no yield between final muzzle capture, placement, preflight, and cast.

## Review status

This is an external review draft, not an applied or tested change. No Unity run, compile, gate, or test was performed here. Root review and the root's focused/broad verification remain pending. The loot golden-capture Unity batch may be active; no project files were edited.

## Root acceptance — 26 October 2026

Applied before this resume; independently reviewed and accepted with R10d. Fresh Unity `RunArcherFlow` passed 123 checks / 0 failures, exit 0, including all three first-contact checks, three actual recipients and unchanged damage/mana assertions. Evidence: `Logs/resume_20261026_02_R10d/summary.json`. The earlier draft-only status above is historical.