# Remote weapons and readable attacks — 9 November 2026

## Scope

User requested held weapons visible to other players and readable attack directions for blocking/parrying. Character model replacement is explicitly deferred. This slice changes cosmetic third-person presentation, not damage, guard eligibility or parry timing.

## Implementation

- Server-owned AvatarCombatWire replicates main/off-hand IDs, effective guard, accepted swing pattern/timing and the authority's followed/committed aim frame. Observers use the shared SwingGeometry and SwingPatternData used for hit resolution. Bounded clock extrapolation smooths samples without predicting indefinitely.
- Retained state supports late join and explicit swing termination: death, weapon switch, wall contact/recoil and normal completion stop the swing. Two-handed weapons suppress the off-hand visual.
- Class skeleton hands carry existing weapon prefabs. Shoulder/elbow posing follows the accepted grip; cosmetic bone edits are restored before the next animation sample. Local first-person body/weapon rendering is unchanged. Dead/extracted remote weapons are hidden.
- Effective guard uses the existing blade pose; shields remain directional blocks and cannot parry. Existing weapon parry rules are unchanged.
- Network protocol bumped 7 -> 8 because the NetworkBehaviour variable layout changed. All peers require the matching build.

## Verification

- Planner checkpoint: Logs/harness/before-remote-weapons.json. Broad 70-gate selection reviewed and narrowed to NetCombat, SweptMelee, ProjectileBlock, Riposte, Hands, Visual: only authoritative pose exposure, replicated cosmetic state and third-person animation changed; no inventory, loot, movement or skill rules changed.
- New AvatarCombatWireTests: 4/4 PASS. Covers wire round trip, bounded clock, cancellation/equipment/guard equality, invalid poses. Evidence: Logs/acceptance/20261109-remote-weapons/editmode/summary.json. Wire unchanged by subsequent cosmetic IK/off-hand visibility corrections.
- First playable compile PASS. Initial host/client review showed held weapons and sword attack; prompted shoulder/elbow and pose-reset corrections. Initial screenshots are diagnostic, not final acceptance.
- Focused run Logs/runs/20261109-134646-984: NetCombat 313/0 (real processes, duel/block/parry/ranged/abuse/enemies), SweptMelee 674/0, ProjectileBlock 51/0, Riposte 144/0, Hands 92/0, Visual 207/0. All six PASS; no broad suite or repeated green fixtures.
- Final playable PASS: Builds/Playable/20261109-134646-984/EmberdelveNet.exe. Real host warrior/client archer review, seed11/protocol8, Logs/acceptance/20261109-remote-weapons/{host-final,client-final}. Ordinary input exercised sword -> crossbow -> sword, bow -> rapier, guard and release, sword LR/RL/overhead combo, client rapier thrust and recovery. Server logs confirm all three sword patterns and accepted client thrust; captured observer screenshots show held weapons, slash/overhead/guard and restored idle hands. First-person host view partly occludes the archer weapon; this is not a claim of new ranged animation quality.
- No exceptions in final review logs. Client12/host14 quit acknowledgements; no surviving EmberdelveNet processes. Main user Editor untouched.
- Scoped source hashes match the mirror. Six inherited paths remain excluded: two Stone materials, Assets/Logs.meta, connection-check image/meta and Pipeline EditorPipelineConfig.json.
- Accepted as a functional remote-weapon milestone; stylized body proportions and final artistic posing remain for the user-requested model replacement. No claim of exhaustive equipment/death/late-join visual acceptance.

## Limits

Current stylized class models and existing weapon assets are retained. This is not an art replacement or new ranged draw/reload animation system. Reach follows gameplay geometry; final human-proportioned models will need grip/pose polishing. Internet latency/loss and capacity are not newly certified by localhost review.
