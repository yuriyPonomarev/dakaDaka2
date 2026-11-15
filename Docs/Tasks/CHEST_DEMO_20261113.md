# Chest looting demonstration — 13 November 2026

User requested live chest-looting demonstration; native allocator investigation deferred by user.
Exact build20261113-143513-195, test-only inventory-contest fixture seed1344668317/port7893 places two actors next to a real Supply chest with one minted common sword. No gameplay source or assets changed. Server25260, visible owner38584, hidden observer40688 started; autoquit1800s. Owner left showing bag-after-loot. These players own the mirror build while alive; close only owned demo processes/quit files before any later mirror tests or builds.

Ordinary review InputSystem actions: look at chest, F open, actual paired UI observed in chest-panel.png; drag from chest cell to bag column6,row1, server accepted exactly one transfer (UID9a722d95bdc04447958a91ad6f811957, corpseRevision1/bagRevision4). Empty chest closes companion panels; Tab reopened bag. bag-after-loot.png visually shows sword at chosen location, bag7/50 (before4/50), carried value83 (before33). User shown both images. No new gameplay acceptance claim beyond this staged demonstration.
Evidence Logs/acceptance/chest-demo-20261113/seed-1344668317/owner/snapshots. Quit files server.quit and owner/quit, observer/quit under same seed folder. Main Unity untouched.
