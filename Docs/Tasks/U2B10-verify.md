# U2B10 verification (P1) — all executed steps PASS

Unity 6000.2.5f1, batch mode, one process at a time. Logs are in `Logs/p1_*.log`. All logs have 0 `error CS`.
(An earlier attempt was blocked by an open GUI editor; the user closed it and the work was rerun.)

Base command: `Unity.exe -batchmode -nographics [-quit] -projectPath <proj> -logFile Logs/p1_<n>.log [-executeMethod Emberdelve.EditorTools.<X>]`

## Step 1 compile
`p1_compile.log`: exit 0, no `error CS`. No fixes were needed in U2B10 code.

## Step 2 BuildDataOnly x2
`EmberdelveBootstrapMenu.BuildDataOnly`, logs `p1_build1.log` and `p1_build2.log`: exit 0, no errors.
- 13 `Affix_*.asset` under `Assets/Emberdelve/Data/Affixes`, 13 unique `.meta` GUIDs.
- Run 2 vs run 1: `git status` of Data, `git diff` of Data and the GUID list are byte-identical (`cmp`), so no further changes.
- Changed assets: `Loot_EmberChest.asset` and `Loot_SealedChest.asset`, plus the new Affixes folder.
- Diff shows Ember `affixRarity` 1,0,0,1 (cuirass, coin, draught, blade) and Sealed 0,2,0 (shield Rare), each with a 13-ref `affixCatalog`.
- Supply table is untouched (all Common, no catalog).

## Step 3 real TryRoll
`EmberdelveAffixProfileGate.Run`, log `p1_profile.log`:
`[Emberdelve][AffixProfile] PASS checks=9977 failures=0`
Key log lines: `rolls uncommon=600 rare=300 commonGear=106 carried=2594 distinct=58` and `seeds303/404/505 seen cuirass=3 blade=3 shield=3 boots=0`.

I added an explicit 303/404/505 block to the gate (additive only). It asserts cuirass and blade have exactly 1 affix, shield has 2 distinct keys, and Common boots have 0 affixes if dropped. The generic loop over seeds 1-300 already checked every drop's affix count and keys against its rarity.

Note: Boots did not drop for seeds 303/404/505 on any table. The "Common boots = 0" check was therefore not exercised by those seeds. Common gear generally having 0 affixes is covered by the 1-300 loop (106 common gear drops), but I did not verify boots specifically.

## Step 4 regressions
| Gate | Log | Result |
|---|---|---|
| AffixDefinition | p1_affixdef.log | PASS checks=169 failures=0 |
| ChestInstance | p1_chestinst.log | PASS checks=21 failures=0 |
| ChestPartial | p1_chestpartial.log | PASS checks=17 failures=0 |
| Smoke:items (RunInventoryFlow) | p1_items.log | PASS checks=403 failures=0 |
| Smoke:inventoryui (RunInventoryUiFlow) | p1_inventoryui.log | PASS checks=158 failures=0 |

## Files changed
- `Assets/Emberdelve/Editor/EmberdelveAffixProfileGate.cs` (added explicit-seed checks)
- Generated: `Assets/Emberdelve/Data/Affixes/` (13 assets + meta), `Affixes.meta`, `Loot_EmberChest.asset`, `Loot_SealedChest.asset`

## Unresolved / not done
- The reopen/transfer UID and rolls check and the live Windows review from U2B10 were not run.
- Nothing committed.
