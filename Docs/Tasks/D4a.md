# D4a — authored immutable DEX movement curve

Accepted31 October2026, based on D3d a08de032. Only locomotion migrates in this slice. Six names/save keys, score bounds1..30, displayed floor modifiers, both class score arrays, HP/mana/regen/damage/ability formulas and item schemas remain. Both classes keep mana. D4 is still in progress; next D4b1 covers swept-melee action speed.

## Implementation

New engine-free PiecewiseLinearCurve copies its input and returns value copies. It validates finite coordinates, at least two strictly increasing scores, interpolates linearly and holds endpoints; NaN input safely selects the first endpoint. MovementCurveDefaults exposes immutable defaults plus fresh authoring arrays. AttributeCurveProfile validates positive factors, captures immutable points and uses the same defaults for missing/invalid data (invalid profiles warn).

The existing data builder generates AttributeCurve_Default.asset and wires missing profile references in both saved class assets, preserving existing authored tables/references. StatsComponent captures at Initialize, then uses one helper across initialization, definition rebuild and instance rebuild. Derived.MoveSpeed, movement queries and effective-stat captures agree. Equipment never reads the mutable SO table again. Walking is clamp(base*curve*multiplier+flat,2,8); sprint/crouch retain authored bases without the walking clamp. Old modifier-based pure APIs keep their explicit semantics for compatibility tests; live stats call the clearly named score API.

| DEX score | 1 | 6 | 10 | 16 | 22 | 30 |
|---|---|---|---|---|---|---|
| Movement factor | .875 | .950 | 1 | 1.075 | 1.135 | 1.175 |

Warrior baseline4.3, Archer4.6225 and Fleet5.315875 are unchanged before held penalties. DEX11 now gives1.0125 and DEX17 gives1.085, including odd scores with unchanged displayed modifiers.

Changed runtime: Rules/PiecewiseLinearCurve.cs and AttributeRules.cs, Data/AttributeCurveProfile.cs and CharacterClassData.cs, Stats/StatsComponent.cs; Movement/PlayerMovement.cs comment only. Builder, two class assets/new profile, new NUnit and AttributeCurve gate, two numeric-fixture migrations, NetMovement route planning and AgentScripts/ReviewD4a.cs complete the change. Runtime root is Assets/Emberdelve.

## Proven fixture migrations

Expectations use independent decimal arithmetic, with old tolerances/coverage retained. Numbers precede held penalties; no runtime output was copied into a golden value.

| Case | Old | New | Reason |
|---|---:|---:|---|
| Warrior boots walk |5.074|5.137425|4.3*1.0125*1.18|
| Warrior boots sprint |5.824|5.8968|5.2*1.0125*1.12|
| Warrior boots crouch |1.7|1.72125|1.7*1.0125; walking flat excluded|
| Archer DEX18 attribute / Fleet |4.73 /5.4395|4.7085 /5.414775|DEX18factor1.095|
| Archer DEX18 Fleet*x1.2+flat1 |7.5274|7.49773|4.7085*1.15*1.2+1|
| Archer boots Fleet walk /sprint |6.2727325 /8.86144|6.3310835 /8.943872|DEX17factor1.085|
| Warrior boots numeric flats .1/.03/.12 |5.174 /5.104 /5.194|5.237425 /5.167425 /5.257425|new boots walk +same flat|

RolledAttributes' DEX14 case and all vital/damage/ability baselines stay unchanged. Independent review caught the omitted crouch migration; root corrected it before execution.

## Executed verification

Twelve selected groups /2,638 checks /0 failures: AttributeCurve82, AttributeRules153, AttributeData28, AttributeVitals191, AttributeAction255, AttributeSkill929, RolledAttributes83, NumericAffix41, EffectiveStats30, InventoryUI485, LoadoutD3c188 and final NetMovement173. Consolidated evidence Logs/d4a_20261031_accepted_summary.json; first11 results in Logs/d4a_20261031_initial/summary.json, final network in Logs/d4a_20261031_net_final/summary.json. The original summary correctly retains its failed initial network entry.

EditMode:280 tests /0 failures /0 skipped, Logs/d4a_20261031_editmode/summary.json. New38 tests cover literal knots/interiors, endpoints, invalid/nonfinite authoring, defensive copies, extreme finite interpolation, score bounds, operation order/clamps, explicit legacy API compatibility and unchanged vital formulas.

The new integration gate double-builds all Data/meta bytes, checks actual class refs, custom/missing/invalid snapshots and both rebuild paths; actual boots on both classes, old capture immutability, no pool refill and literal CharacterController walking including held factors.

NetMovement freshly builds a Windows player and runs a real server plus four clients. Its first run171/172 exposed a fixture assumption: W at(13,7) walked north into D3d's actual cache at(13,11), stopped atz10.280, while the fixture expected the wall atz12. The route planner previously ignored chests. It now includes the actual body and parent-scaled lid footprint, while the target wall measurement still uses level geometry. A subsequent7/1 route-only attempt exposed accumulated float stepping excluding the exact3m south wall (2.999999<3). Integer-indexed0.1m samples correct this without changing any wall-selection/arrival tolerance. Final route north is rejected, south yaw180 /3m is selected; full173/0. Both failed logs are retained in initial and net_route directories. No gameplay/collision/network rule was changed to satisfy the gate.

Actual character screenshot inspected: Logs/d4a_20261031_character.png. Warrior boots display DEX11(+0) and walk5.14m/s; measured captured walk5.13742542, physical walk4.82918 after held penalties. Frames advanced182→868. Visual fixture is Play-mode-only and saved no scenes/assets. The first attempt found a character already killed while logs were being reviewed; a fresh scene supplied the accepted result. CLI measurements in Logs/d4a_20261031_visual_prepare.json. Own Editor was cleanly closed and temporary background override removed.

Independent read-only source review approved runtime/builder/tests and the exact Network route/precision repairs. Reviewer ran no tests or visual tools; root executed the evidence. No server combat/full raid/release acceptance is inferred.