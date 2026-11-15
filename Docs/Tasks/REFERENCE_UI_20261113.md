# REF-UI — Dark and Darker inspired interface, 13 November 2026

User switched priority from combat to interface. Preserve all earlier systems. Checkpoint: Logs/harness/before-reference-ui-20261113.json.

Scope: presentation-only pass on existing lobby, preparation, inventory and HUD. Shared original grain, brass borders, Cinzel headings, readable opaque hover/press colors. Lobby separates connection/team settings on left and class choice on right, scales its existing pixel layout as one unit. Preparation keeps existing equipment/merchant/stash panels and all transaction handlers. No changes to combat, authority, item persistence or inventory command model.

Reference: publisher's Steam screenshots, https://store.steampowered.com/app/2016590/Dark_and_Darker/ ; stash screenshot retained outside Assets in Logs/acceptance/reference-ui-20261113/original-stash.jpg. Similarity target: dark framed panels, subdued gold, serif headings and grid hierarchy. This is not full original UI parity: no tavern lobby scene, no equipment-dependent preview in preparation, original item icons remain silhouettes.

Verification: planner conservatively selected 70 gates because new/shared UI files are unknown. Per AGENTS cosmetic changes require compile and actual screen/input proof; no repeat of green combat/item tests and no cosmetic unit tests. Build and bounded visual/input results recorded below; no commit.

## Verified result
- Builds105323-147,105640-751 and final105957-378 PASS. Final playable Builds/Playable/20261113-105957-378/EmberdelveNet.exe. Only inventory footer spacing/text and backdrop opacity changed after105640; all other UI observations from105640 remain applicable.
- Actual rendered images inspected via development Player review bridge (ordinary virtual InputSystem mouse/keyboard, no direct UI handler invocation). Evidence Logs/acceptance/reference-ui-20261113.
-105640 final-1440: lobby portraits/connection/class cards; prepare; purchase Blood Draught (150 ->114 gold, new stash item); Shift+RMB transfer (stash cell clears, bag stack becomes2); ordinary enter Royale seed11; HUD after spawn; Tab inventory.
-105640 final-1280:1280x720 lobby fits, ordinary Duo click -> preparation title DUO. Full three panels and departure button fit. Developer-build watermark remains in lower-right margin.
-105957 verified-1280: ordinary prepare/depart seed11; inventory footer reviewed idle and selected; BAG -> CHARACTER -> BAG; potion selected and moved to exact empty cell, original cell empty and occupancy remains4/50; tooltip stays inside panel; Tab closes inventory and HUD returns. Screenshots inventory.png, character-tab.png, selected.png, moved.png, hud-ready.png.
- Fixed real review findings: preparation backdrop now opaque (no lobby text bleeding through); grain reduced; inventory instructions now explicit two lines with bottom inset. Added isolated cosmetic class portrait cameras using existing project models; they contain no live PlayerRig/authority state.
- No managed exceptions/errors in the three inspected final player logs. No full gameplay suite rerun for cosmetic work. This is bounded UI/input acceptance, not complete original-game parity or a new integrated gameplay baseline.
- Review players21992,37632,42640,27992 closed by bridge quit. Newly spawned PickerHost prompts41308,33992,17580 identified by name/start time and dismissed by process termination under user's prior authorization; firewall permissions/rules unchanged. Main Editor untouched.

Remaining fidelity: tavern lobby environment/navigation, richer item artwork, equipment-aware preparation preview, finer HUD layout and ornaments. Existing inherited mixed work remains uncommitted; no baseline/milestone acceptance inferred.
