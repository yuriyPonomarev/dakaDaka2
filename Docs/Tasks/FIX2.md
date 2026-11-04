# FIX2 - parry did not work in the real game; a held block takes 0 HP (4 Nov 2026)

Status: implemented, uncommitted (on top of U3C3 de67cce2).

## Root cause of NoParry
The user's build (host, warrior with `ashen_short_sword`) logged blocked enemy hits followed by `Rejected riposte: NoParry`. The block WAS resolved by the real path (EnemyAgent swing -> `ApplySwingHit` -> `NoteMeleeResult`), but it was never recorded as a parry because the warrior carries the Emberbulwark shield in the off hand: `DefenseProfile.HasShield` is true, `PlayerRig.GuardHasShield` is true, and `RiposteRules.KitCanRiposte` refused any kit with a shield contributing to the guard (my U3C3 rule "not available with a shield"). The reported `blockReduction=0.820` is the shield kit (class 0.7 + Bulwark Training 0.12). The U3C3 gate used fake fighters without a shield, so the warrior kit was never exercised. Only the archer's rapier (no shield) could ever parry.

## Decisions (flag for review)
1. **The parry is the sword's.** New data flag `WeaponData.riposteWithOffhandShield` (default false, true on ashen_short_sword via `EmberdelveAssetLibrary.ApplyCombatFlow` and the asset). A flagged sword may riposte with a shield in the off hand; a shield on its own (weapon is a shield, or an unflagged weapon) still never parries. Set the flag to false on the sword asset to restore "no parry with a shield" with no code change.
2. **A held block takes 0 HP.** `BladeGuardRules.HeldGuardReduction = 1` is the reduction of a guard that HOLDS (inside the arc, right height, impact power below the resist). A broken guard keeps `authored reduction x guardBreakReductionScale` (unchanged partial). Authored `blockReduction` (class 0.7, rapier override 0.7, Bulwark Training +0.12) therefore only matters for broken guards now.
   - `DamageInfo.WithReduction`: a non-broken full reduction gives Amount 0 instead of the 1.0 floor; `IsValid` accepts exactly 0 only for a held block; `ApplyArmorRating` and `ApplyProjectileFalloff` leave 0 at 0 (their 1.0 floors would have lifted it).
3. **Damage and the parry window.** Only real damage (Amount > 0) cancels a live parry. A held (0 HP) melee block REFRESHES the parry (new block time, new window); blocked projectiles neither create nor cancel one (nothing was taken). A broken guard or any unblocked hit cancels it.
4. A block press with no parry on record is just a guard being raised: it is no longer logged or counted as a rejection (`RefuseRiposte`, `MeleeCombat.TryBeginRiposte`).

## Tests that would have caught it
- Smoke `RiposteRealPathFlow` (Run-All `RuntimeSmokeTest.RunCombatFlow`): the real PlayerRig of the class (warrior: sword + shield) is driven exactly like a remote player's server rig (`MeleeCombat.RemoteDriven` + `SetRemoteGuard`), a real EnemyAgent swings its real swept attack at it, the server records the parry against that enemy, the player lost 0 HP, a release + press starts exactly one riposte of the data's strike count with strike numbers 1..n, and the enemy is hurt.
- NetCombat gate session `ParrySession` (two real player processes, new script `melee-parry`): A swings once, B (warrior, sword and shield) holds, takes 0 HP (`amount=0 blocked=True guardBroken=False blockReduction=1`), releases and presses again, the server logs `combat-swing id=B ... riposte=1` and `riposte=2`, exactly the data's number of hits land on A, each with the data multiplier and a data-derived amount, no combat reject, clients apply nothing.
- Riposte gate: block takes 0 HP, sword + shield parries (data allows it), a sword without the allowance refuses with a shield.
- EditMode: held vs broken reduction rule, `DamageInfo`/armor/falloff at 0, the shield-allowance matrix, data flags (`RiposteDataTests`), the `melee-parry` script kind.
- Expectations that derived from the kit reduction now derive from `HeldGuardReduction` (BladeDefense, CombatFlow, ArmorAffix gates; BladeDefenseRulesTests; smoke front-facing block: 0 for a held guard, the partial for a broken one; NetCombat `GuardedAmount`).

## Files
Runtime: `Core/GameContracts.cs`, `Combat/DamageAndScanning.cs`, `Rules/BladeDefenseRules.cs`, `Rules/RiposteRules.cs`, `Combat/LocalCombatAuthority.Riposte.cs`, `Combat/MeleeCombat.cs`, `Data/WeaponData.cs`. Net: `NetRules/NetScriptSpec.cs`, `Net/NetCombatSender.cs`. Editor: `EmberdelveAssetLibrary.cs`, `EmberdelveRuntimeSmokeTest.cs`, `EmberdelveNetCombatGate.cs`, `EmberdelveRiposteGate.cs`, `EmberdelveBladeDefenseGate.cs`, `EmberdelveCombatFlowGate.cs`, `EmberdelveArmorAffixGate.cs`. Tests: `BladeDefenseRulesTests`, `RiposteRulesTests`, `Combat/RiposteDataTests`, `NetRules/CombatNetScriptAndArgsTests`. Asset: `Weapon_AshenShortSword.asset`.

## Checks
EditMode 668/668; fast gates 12/12; Riposte gate 144/0; NetCombat 272/0 (221 + 51 new); RuntimeSmokeTest.RunCombatFlow PASS. Full `Run-All -Set all -EditMode`: RESULT PASS (EditMode 667/0 in the batch run, 65/65 gates, smoke 247/0; `Logs/git/runall_fix2.log`).

## Compromises
- Enemies do not riposte; no HUD cue.
- The riposte real-path smoke boosts the enemy's health for the proof (two strikes x1.5 would kill a minion) and restores it.
- With 0 HP held blocks the FP hit-react arms do not play on a block (`FirstPersonArmsDriver` ignores Amount <= 0); a block-react cue is a later presentation task.
