# Enemy blood feedback

> Historical implementation note. Current acceptance and remaining work: [CURRENT_WORK](../CURRENT_WORK.md) and [raid acceptance](RAID_UI_ACCEPTANCE_20261108.md). Status statements below describe the original slice.


Enemy visual prefabs include a world-space blood burst at the authority-provided hit point for positive damage, including lethal hits. The previous cone followed DamageInfo.Direction into the target, where the mesh could hide the particles; the cone now sprays back out from the impact. Droplets are larger, brighter, and last longer so the burst reads at combat distance. Invalid or distant hit positions still fall back to the enemy. A held block with zero damage emits nothing. The effect is presentation only; combat and health are unchanged. The prefab builder recreates the effect on future rebuilds.

The attack flinch suppression window now uses the actual clip playback duration, including its speed multiplier.

Checks: mirror EditMode 796/796 passed; Visual gate 193/0 passed; NetCombat gate 290/0 passed, including delivery of the same authoritative blood event and point to both clients. The full 68-gate set was stopped after 16 gates because its broad run was taking disproportionate time. A running-game graphical review remains outstanding: opening the mirror GUI reached an approval timeout, and no Unity terms were accepted.
