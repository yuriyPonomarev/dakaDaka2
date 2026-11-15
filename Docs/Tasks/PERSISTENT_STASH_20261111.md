# STASH-01 offline preparation and quartermaster

User explicitly authorizes persistent stash, first merchant and equipping extracted items; clarification: OFFLINE FIRST. This overrides old deferred-stash scope. No network inventory/profile trust changes.

Acceptance: fresh class profile -> prepare kit -> raid -> extract same UIDs/affixes -> return to lobby -> stash/equip/sell/buy -> restart application -> enter another raid with chosen kit. Death/abandon loses only carried kit. Storage and gold survive. Disk failure refuses mutations; stale writes and repeated extraction cannot duplicate rewards.

Design: local per-class profiles, 12x20 stash and 10x5 raid bag, gear paper doll; quartermaster sells common class weapons and supplies, buys loot. Initial kit and 150 gold once per class. Transactions commit the complete profile atomically before exposing changes. In-flight kit removed from ready inventory before departure; crash counts as abandonment. Corrupt files fail closed, kept for recovery. Prices shown before explicit purchase/sale.

Ownership: new Items/OfflineProfile model/store and OfflineProfileSession; new UI/RaidPreparationScreen; existing LobbyController/LobbyScreenView, PlayerRig, RunDirector integration. Tests in Combat fixture. Preserve all inherited dirty work. Checkpoint Logs/harness/before-persistent-stash.json.

Checks: profile persistence/transaction/identity tests; existing Inventory/Hands and extraction regressions; offline live preparation lifecycle; final build on DakaDaka_ci only. Main Editor is never closed or put in Play.

Reference pass: inspected live Dark and Darker stash and Squire Service/Base Gear screen. Stash uses 12x20 cells; migrated the draft 10x12 profile without moving or deleting items. Equipment/bag on left, stash on right. RMB equips/removes, Shift+RMB transfers, drag ghost follows pointer. Squire supplies class base weapons into empty slots; supplied equipment cannot enter stash or yield gold until extracted. Consumable/armor base presets and multiple merchant services remain outside this first merchant implementation.

Initial proof: 997/997 EditMode pass at Logs/runs/20261111-234112-688 before reference refinements; this does not validate later changes. Network gates/build still running.

Final evidence (12 November): player build succeeds at Builds/Playable/20261111-235408-564/EmberdelveNet.exe; Hands 92/0. Full EditMode 998/999 passed at Logs/runs/20261111-235408-564; the only failure was fixture setup (EditMode does not call PlayerMovement.Awake). Corrected fixture only, reran OfflineProfileTests: 9/9 pass at Logs/acceptance/stash-profile-final. Combined evidence covers all 999 tests, not a fresh all-green aggregate. Final runtime source matches that build; later changes only fix test setup/docs. Initial network regression proof remains NetInventory148/0, NetExtraction136/0, NetFinal172/0 at 20261111-234112-688; later changes are offline-gated profile/UI and offline ApplyClass hook.

Live reference: original Stash and Squire Service/Base Gear screen observed through CUA. Prototype player launches to lobby with isolated --ember-profile-dir Logs/acceptance/stash-ui-final/profiles. Windows firewall PickerHost prompt overlays the player and is absent from CUA window inventory; coordinate input refused because PickerHost owns the point. Raise/Escape and app discovery did not dismiss it. User is away and asked agent to cancel; no available CUA target for that system dialog. Did not grant network access or change firewall. Mouse acceptance therefore BLOCKED, not claimed complete. Player window left open for review. Main Unity Editor untouched; no commits.

Implemented scope: persistent per-class stash, exact-cell moves, equipment, merchant purchase/sale, death/abandon/extraction durability, supplied base weapons (no pre-extraction stash/sale profit), rollback on full destination or failed disk writes. Reference parity limits: no 3D character preview, multi-tab stash, merchant quests/affinity, physical gold containers, configurable base armor/consumable presets. Prices use our item catalog. First merchant and offline raid loop only.

Next: dismiss the Windows prompt and verify Prepare Raid -> buy -> drag -> RMB equip -> sell -> raid -> return -> restart persistence visually. No repeated passing test suites needed solely for that review.

## Live continuation - 12 November 2026

The firewall blocker was removed by terminating only verified PickerHost permission-dialog processes (11140, later 10036 for the new executable). No firewall/network permissions changed. Main Unity Editor was not closed, played or switched to another scene. No active mirror runner existed before the one build below.

Evidence: Logs/acceptance/stash-ui-20261112. Real isolated profiles remain at Logs/acceptance/stash-ui-final/profiles. Existing gameplay implementation was preserved. Initial live review used 20261111-235408-564; the new build differs only by the Development-only review drag support described below.

Verified in the running Player:
- Purchase Blood Draught: 150 -> 114 gold; item appears in stash. Exact-cell click placement to bag, RMB unequip/re-equip sword, sale at displayed +9: 114 -> 123 gold.
- STORE BAG followed by a real process restart preserves 123 gold, stash contents and equipment. Additional purchase leaves 87 gold.
- Departure reserves ready gear/bag, preserving stash. Forced termination during the raid and restart loses the carried kit, keeps 87 gold and stash items. Squire refills empty weapon slots.
- A second actual raid restores exactly the prepared single bandage and base equipment (prepared-in-raid.png); no starter consumables are reseeded. Enemy death clears the active raid and ready kit, preserving the stored torch UID 25a0bc8d64f24253813edaf422644a27 and gold (after-death.json). Return-to-lobby works.
- Archer has its own initial 150 gold and class kit (archer-separate-profile.png).
- Reliable drag through ordinary InputSystem mouse events moves that torch from stash to bag cell (4,2), preserving its UID (bridge-v2/snapshots/drag-no-preselect.png). An out-of-bounds drop refuses without moving/deleting it; ghost remains for retry, Escape cancels (drag-refused-edge.png, drag-cancel-base.png).
- Supplied sword cannot enter stash before extraction; UI reports the reason and sale value 0 (supplied-stash-refused.png).

Native CUA clicks worked, but its drag without preselection and short keyboard presses did not reliably reach Unity. To disambiguate game behavior, added 29 lines to Runtime/Review/EmberdelveReviewBridge.cs: optional mouse drag/endX/endY, finite in-screen destination validation, 24 held pointer steps and normal release. Still opt-in --ember-review-dir, Development/Editor only; no direct UI/gameplay mutation. Local Send.ps1 in evidence folder records each ack. This is review tooling, not a gameplay fix. Checkpoint Logs/harness/before-stash-visual-20261112.json. Planner selected all conservatively for review code; narrowed to build + actual input-path proof because no game rules changed. Build PASS at Logs/runs/20261112-002608-408; playable Builds/Playable/20261112-002608-408/EmberdelveNet.exe. No passing gameplay suites rerun. Existing combined 999-test proof is unchanged, not a new aggregate.

Limits/open findings:
- Two traversal attempts ended in enemy death. Seed 11 / hash CC18B0DC collected one loot item (value 10), passed two rooms and died at 00:40; this is NOT successful extraction acceptance. Prior automated extraction identity proof remains valid, but live success -> lobby -> restart -> reuse still needs completion.
- Native Unity error `Invalid memory pointer was detected in ThreadsafeLinearAllocator::Deallocate!` occurred in the old long-running player and the freshly launched new build (review-v2.log:137), exposing the Development Console. Cause not established; do not dismiss it as merely an overnight-process issue or claim it fixed.
- Lobby gear score can retain the last raid's value after death (observed 6); preparation itself correctly shows empty gear until Squire is used. Follow-up UI correction remains open.
- STASH-01 is partly visually verified, not accepted/committed. Final test Player is left in preparation (PID 20912; rediscover before use). Warrior profile revision 19, 87 gold, no active raid, empty stash/bag after the torch was taken into and lost in the last raid, freshly supplied weapons. Archer profile unaffected.

Next: complete controlled live successful extraction/restart with actual prepared UIDs, investigate allocator error and stale lobby gear score, retain existing evidence and unrelated work. Do not repeat all green gameplay tests for documentation alone.
### 12 November follow-up: goal, label and native-render diagnostics

User explicitly authorized an active goal: finish STASH-01, then reference-faithful combat work, then textures/models/environment/light. No claim of complete Dark and Darker parity.

Checkpoint Logs/harness/before-stash-finish-20261112.json. Removed the unqualified historical GearScore label from LobbyScreenView; Last run now explicitly includes that run's score. Updated existing RarityLadder UI assertions. Run Logs/runs/20261112-004400-405: RarityLadder917/0, RetroVisual104/0, Build PASS. This is not a repeat of the full EditMode suite.

Native allocator diagnostic evidence: Logs/acceptance/stash-finish-20261112. DX12 reproduced Invalid memory pointer / JobTempAlloc with native ScriptableRenderContext.Cull stack. Short DX11 run had no runtime invalid-pointer error but still two cleanup allocation warnings; API change is not accepted as a fix. Experimental positive ParticleSystemRenderer.minParticleSize also reproduced the error and was removed from source and mirror. Build004400 contains that failed experiment; do not label it a fixed or exact-current-source release. No Unity/package upgrades or permanent graphics API changes.

Test mirror Editor PID37960 was opened with isolated editor-profiles/editor-review paths, Lobby played with advancing frames. CUA rejected access to the Unity window; synthetic mouse did not prove the preparation transition in Editor. Play stopped via mirror-targeted CLI. No extraction acceptance claimed. Test Player13772 quit normally; previous players33600 and20912 also closed. Main Editor12696 remains untouched. Mirror Editor remains open/stopped: do not start batch harness until it is closed by its exact project path. No active batch runner and no commits.

### Successful live cycle — 12 November 2026, build005329

Exact-current-source build PASS: Logs/runs/20261112-005329-768, Builds/Playable/20261112-005329-768/EmberdelveNet.exe. Build-only after removing the failed particle experiment; no already-green gameplay suites repeated. Prior label gates remain applicable because their source did not change.

Normal offline raid, seed11, Archer, no teleport/invulnerability/staged exit: ordinary InputSystem movement, doors, loot interaction, pauses between inspection steps and held F extraction. Evidence Logs/acceptance/stash-finish-20261112/raid-archer2. Took Arrow Bundle (+10), escaped in displayed00:23, result YOU ESCAPED THE DELVE. Durable profile revision6, gold150, activeRaid empty, supplied empty. Equipment snapshot checksum exactly matches departure. The earlier raid-archer attempt died at00:42 after taking loot and is kept as failed traversal evidence.

Returned to lobby: Last run explicitly reads Extracted / Archer / value10 / gear score4 (lobby-after-extract.png). In preparation, RMB removed the extracted bow into bag; STORE BAG put bow and recovered arrows into stash (extracted-stored.png). Quit the Player, started a new OS process with the same isolated profile, selected Archer/preparation: stash restored (restart-success/snapshots/restored-stash.png). Profile file hash before/after real restart is identical. RMB equipped the extracted bow directly from stash (reequipped-extracted-bow.png). Original and final UID both e9d54b499117461cb6eafeff8d934b2a; gold150; activeRaid empty. Structured proof restart-success/identity-proof.json, before/after profile snapshots retained. Consumable Arrow Bundle is stackable and has no instance UID by design; the bow provides exact identity proof.

STASH-01's offline purchase/preparation/death/crash/successful extraction/stash/restart/re-equip cycle now has live proof combined with the existing focused transactional tests. This is functional acceptance of the bounded offline feature, not full Dark and Darker economy parity or whole-project release acceptance. No commits: inherited changes in shared files require coherent integration separation. Native rendering issue remains open: DX11 avoids observed invalid-pointer console error in this cycle, but JobTempAlloc warning still occurs (raid-archer2/player.log:210 and cleanup); do not call DX11 a fix. No permanent graphics API changes.

Mirror Editor37960 closed gracefully by exact project path before build005329. Main Editor untouched. Reference-combat research is recorded separately in COMBAT_REFERENCE_20261112.md and is the next implementation slice.

## Carried-ammunition extension, 12 November
Offline envelope v2 and explicit loaded/carried/stored ammo now extend STASH-01. See combat task report/current work for source/evidence and remaining cross-player ownership defect. Exact playable073304-224, live carried-ammo-live-20261112: purchased/unpacked arrows, prepared bow5/reserve3/stash8 -> real shot/reload5/2 -> successful ordinary seed11 extraction -> stash loaded bow -> OS restart with identical profile hash and same UID/ammo. Loaded sale refused, Squire empty weapon, reserve-store conservation confirmed. Structured ammo-proof.json. Focused automated transactional/runtime and five integration gates pass as separately recorded. This accepts bounded solo ammo persistence; cross-actor loaded weapon transfer is not accepted. No commit/global economy parity claim.
