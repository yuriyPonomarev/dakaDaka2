# U1C2F-R1 — accepted for requested locomotion bound

Independent review 19 October 2026. Worker settled; actual controller/Pi absent before source/build review. Reviewed the single non-dash horizontal-speed clamp after the existing acceleration calculation; minMoveDistance=0, dash early branch, class values and collision configuration remain intact.

Fresh Unity6000.2.5f1 Windows build validation/U1C2A-windows-20261019-075123: exit0, unique positive BuildWindows PASS/resultSucceeded, valid executable/Data, no compile failures. Actual Player30308 ordinary boots sprint/contact request remained <=5.824m/s, replacing prior34.663m/s request at legal5.824. Fresh corridor-south-offset W+Shift2s moved11.2045m; chapel-east2.7s moved15.2482m; second-run north2.75s moved15.5358m. Wall/body contacts remained real Sides collisions. Released-input commands sampled zero intention/displacement. All inputs queued normally through InputSystem; no body/health/loot writes.

Actual controller-reported physical velocities still spike at contacts (up to190m/s observed); acceptance concerns requested velocity only, not a physical-velocity bound. Controlled30/60FPS and dash-specific regression remain future targeted checks; no FPS cap was introduced. Subsequent complete actual local run reached extraction (second-run-gate-hold.png), returnedLobby and quit normally.

Evidence: manager validation/U1C2A-player-20261019-075204 commands and saved sequences. Original F highFPS starvation acceptance is restored together with this narrow overspeed repair. Parent U1C2 remains open until remaining container acceptance.
