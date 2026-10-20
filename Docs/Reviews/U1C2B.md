# U1C2B — accepted (input processor repair only)

Independent source diff: exactly Scale2 -> ScaleVector2 at fallback.cs66 and saved EmberdelveInputActions.inputactions246. Saved asset correction is justified: current saved dungeon scene references that asset, so fallback-only repair would leave the shipped error. No binding IDs, paths, groups, multipliers or unrelated source changed. Installed InputSystem InputManager1952 registers ScaleVector2.

Fresh Windows build exit0, exactly one PASS result=Succeeded, binary/Data layout valid, no captured build errors/timeouts. Evidence manager review-U1C2B-build.json, validation/U1C2A-windows-20261019-053118. Fresh actual windowed Player27036: Lobby initial frame2/time.02; ordinary queued mouse input clicked DESCEND; saved dungeon scene screenshot frame12525/time10.95. Inspected processor-fixed-raid.png1280x720: prior development console error overlay absent. Full player.log search Scale2/No InputProcessor/Exception/Error/FAIL finds none. Quit id3 acknowledged, actual owned Player and Editor PIDs absent afterward.

Evidence validation/U1C2A-player-20261019-053239 (launch, commands/ack, player.log, snapshots). Keyboard/mouse/menu path verified; gamepad hardware sensitivity not measured. Existing clipped HUD/overlapping skill labels/offscreen equipment remain U1C2C; no broader visual/game-loop acceptance.
