# UTP_RESET: the dedicated server stops receiving from everybody after one client leaves

Status (round 2): the fault is REPRODUCED deterministically (section 8). Round 1 mitigations did not work: the connreset fix was never applied (the socket was not found) and the client linger cannot work. Round 2 (section 9) replaces the socket search and adds two alternative socket layers behind `-ember-net-udp-path`. Everything in round 2 was written on Linux: NOT compiled, NOT run. Sections 1-7 are the round 1 text, kept for the reasoning; where section 4(a) and section 9 disagree, section 9 is current.

## 1. Symptom

Seen twice in CI (Windows, loopback, `com.unity.netcode.gameobjects` 2.7.0 + `com.unity.transport` 2.6.0):

| run | trigger | what the server log shows |
|---|---|---|
| `net-admission/20261028-150012` | client B hard-killed (Process.Kill) | B removed at tick 472 (the 4000 ms timeout). A's last accepted frame is tick ~471-473. Nothing from A after that, nothing from D (never reached `approval`), until the end of the run. |
| `net-final/20261028-155557` | client fin-d quit gracefully (quit file, process exit 0) | fin-d's last frame tick 1318; A (id 1) last frame 1325; C (id 3) last frame 1332; `rx-quiet` lines at ticks 1348 / 1355 / 1362. Then `client_b2` gets `connect timeout after 60s`. |

In both: the server process is healthy (ticks advance, `status` lines every 2 s, UTP timers fire: it times every client out after 4000 ms, the quit file works), no exception, no `Error`, no transport-failure line in any log. The server never reported receiving anything again.

Facts worth keeping apart from the hypothesis:

1. The two runs differ in trigger (hard kill vs graceful quit). In `net-final` an EARLIER hard kill (B, removed at tick 1071) was harmless: fin-d was admitted afterwards. So "a hard kill kills the receive path" is not deterministic.
2. In `net-final` the server never saw fin-d's Disconnect: fin-d's `client disconnected id=5` and removal come at tick 1435, i.e. its 4000 ms timeout counted from tick ~1318, although fin-d logged `shutdown complete clean=True` and exited. So the receive path was already dead when fin-d's disconnect packet arrived (or died on it).
3. In `net-final` the three "last frame" ticks are not identical (1318, 1325, 1332). A and C kept sending; the quietness measure is "last accepted frame", and a player that stands still sends few frames, so a spread of a few hundred ms is within what the measure can show. It does not contradict a single death at ~tick 1318-1332, but it is not proof of one instant either.
4. In `net-admission` the death coincides with the server-side REMOVAL of B (4 s after the port closed), not with the kill.

## 2. Hypothesis (leading, unproven)

Windows reports an ICMP "port unreachable" (the peer's UDP port is closed) as `WSAECONNRESET` on the next receive of the unconnected UDP socket that sent to that port, unless `SIO_UDP_CONNRESET` is switched off (`WSAIoctl(sock, 0x9800000C, FALSE)`). A server that serves many peers from one socket gets one peer's reset on the shared socket. If baselib's receive path treats that error as terminal for the socket, the server stops receiving from everybody while the managed driver keeps running its timers: exactly the symptom. The public forum thread "Unity Transport 6.6.0: UDP host stops receiving after abrupt client disconnect on Windows" (raw `NetworkDriver`, loopback, no fix posted) describes the same thing.

What fits: (2) above (graceful quit: the server sends the leaving client ~30 Hz of ack / NetworkVariable traffic; the client closes its socket right after sending Disconnect; a send landing in that window produces the ICMP, and the Disconnect itself is lost with the dead receive path).

What does NOT fit cleanly and must not be forgotten:

- Rarity. Windows generates the ICMP for loopback sends to a closed port reliably, yet most client departures in the earlier gate runs were harmless. Either the error is only fatal in a race (a receive posted at that moment), or another mechanism is involved.
- Timing in `net-admission` (fact 4). The server sends heartbeats to a silent peer (HeartbeatTimeoutMS = min(500, 4000/4) = 500 ms), so if heartbeats go to B's closed port the first ICMP arrives within ~0.5 s of the kill, yet the death came 4 s later, at removal. It fits only if the FIRST datagram to B's closed port is one sent at removal (for example a Disconnect sent when NGO removes the connection). Unknown, not visible in the logs.
- A fault tied to the connection removal path in UTP/baselib (not to ICMP) would give the same logs. The probe separates the two: arm `a` removes the ICMP effect, the control `baseline` keeps it.

## 3. What can be said about the package API (reasoning from public API; the package source is not in this snapshot)

- `UnityTransport` (NGO 2.x) public knobs: `DisconnectTimeoutMS`, `HeartbeatTimeoutMS`, `ConnectTimeoutMS`, `MaxConnectAttempts`, `MaxPacketQueueSize`, `MaxPayloadSize`, `MaxSendQueueSize`, `SetConnectionData`, `ConnectionData`, `SetDebugSimulatorParameters`, `UseEncryption`, `UseWebSockets`, `GetCurrentRtt`, `GetLocalEndpoint`. None of them is a socket option. The game already sets the two timeouts.
- `NetworkSettings.WithBaselibNetworkInterfaceParameters(receiveQueueCapacity, sendQueueCapacity, maximumPayloadSize)` (transport 2.x) is the receive-queue capacity knob. NGO builds the settings itself (from `MaxPacketQueueSize` and friends) and gives the game no hook to pass its own `NetworkSettings`, so it cannot be tuned from game code without a custom driver constructor. A full receive queue drops packets; it is not a permanent-death mechanism, and the symptom here is permanent, so this was NOT pursued.
- UTP has no public socket-option or "ignore connection reset" setting that I know of. UNCERTAIN: a newer transport may handle `WSAECONNRESET` internally; the installed 2.6.0 is the one in the manifest.
- Does NGO's `UnityTransport` expose the `NetworkDriver`? UNCERTAIN. I believe the driver is a private field (`m_Driver`) with an internal accessor; I could not verify. The code therefore does not compile against it: it finds a field or property whose type is named `NetworkDriver` by reflection.
- Even with the driver, the OS socket is not reachable by field access: in transport 2.x the baselib interface keeps the socket as a native `Baselib_RegisteredNetwork_Socket_UDP` behind a pointer / NativeArray, not as an OS `SOCKET` value. So the reflection walk is expected to find nothing; it is kept only because it is cheap and harmless, and every candidate it returns is verified with `getsockname`.

## 4. Design

### (a) Server, Windows only: `NetUdpResetGuard` (`Assets/Emberdelve/Net/NetUdpResetGuard.cs`)

- Called from `NetBootstrap.OnServerStarted` (dedicated server and host), after the transport bound its port. Retried every 0.5 s for 3 s if nothing was found yet.
- Finds the socket: (1) reflection candidates, each verified; (2) otherwise probe the process's own handle values 4, 8, ... 65532 with `getsockopt(SO_TYPE) == SOCK_DGRAM` and `getsockname` port == the server port. The scan runs on a worker thread with a 3 s limit so a strange synchronous handle can never freeze the server's main thread.
- On every match: `WSAIoctl(SIO_UDP_CONNRESET = 0x9800000C, FALSE)` and `WSAIoctl(SIO_UDP_NETRESET = 0x9800000F, FALSE)`.
- try/catch around everything; non-Windows builds compile to a stub that reports `skipped`.
- Log lines (all `[Emberdelve][Net][server] `):
  - `udp-connreset disabled sockets=N port=P` success (N = sockets where SIO_UDP_CONNRESET succeeded)
  - `udp-connreset NOT applied: <reason>` (no socket found / ioctl error with `wsaerror=` / exception)
  - `udp-connreset skipped: not a Windows build`
  - `udp-connreset fix off (-ember-net-udp-reset-fix false)`
  - `udp-connreset detail handles=[..] netreset-ok=K reflect-driver=found|none reflect-candidates=.. reflect-verified=.. probe=done|timeout scanned<=65536`
- Switch: `-ember-net-udp-reset-fix [true|false]`, default true.
- UNCERTAIN: that the ioctl is honoured on a baselib registered-I/O socket. `SIO_UDP_CONNRESET` is a property of the socket, so it should be; if baselib uses RIO the reset would surface as a failed receive completion and the ioctl stops it being generated. Not verified. UNCERTAIN: that the ws2_32 `SOCKET` for baselib's socket has a value below 65536 (handle values in a normal player are far lower).

### (b) Client linger: `NetBootstrap.ShouldLinger`

- After a REQUESTED shutdown finished (`ShutdownInProgress` false) and before `Application.Quit`, a client waits `-ember-net-client-linger-ms` (default 250; 0 = off; capped by the existing 5 s shutdown deadline). Not used for a lost connection, a failed connect, or a lobby-started session that returns to the lobby. Log: `[Emberdelve][Net][client] linger ms=250 reason=quit-file`.
- Honest limit, stated so nobody over-trusts it: NGO closes the client's UDP socket inside `Shutdown()`, which is what `ShutdownInProgress` waits for. The linger happens AFTER that, so it cannot keep the port open and cannot stop an ICMP. At most it lets the process stay alive (and any OS-level pending send finish) a little longer. It is the candidate the task asked for; the probe arm `b` measures whether it does anything. If it does not, remove it. A mitigation that actually closes the window needs the server to learn about the departure BEFORE the client's socket closes (the client asks the server to drop it, waits for the server's own disconnect, then closes). That changes the protocol and was not done.

### Defaults

Both on. There is no probe evidence yet that says otherwise; the probe's job is to produce it.

## 5. The probe: `Emberdelve.EditorTools.EmberdelveUtpResetProbe.Run`

```
Tools\Run-EmberdelveGates.ps1 -ProjectPath . -Methods Emberdelve.EditorTools.EmberdelveUtpResetProbe.Run -TimeoutSeconds 3600
```

Optional editor args: `-ember-probe-trials N` (8), `-ember-probe-arms baseline,a,b,full` (default `baseline,full`), `-ember-probe-window SECONDS` (8), `-ember-proof-reuse-exe ABS_EXE` (a NetProof build that has the UTP_RESET switches), `-ember-proof-dir ABS_DIR`. Expected duration: about 35 s per trial plus a build (~10 min for the default 2 arms x 8 trials; the default runner timeout of 900 s is too short).

Per arm there is a session: a fresh server (max players 3, no enemies, 4000 ms timeout, `-ember-net-udp-reset-fix` as the arm says) and client A that stays. Each trial n (1-based): odd = X leaves GRACEFULLY (quit file), even = HARD (Process.Kill).

| stage | pass condition |
|---|---|
| `x-connect` | X admitted and logs `connected` |
| `x-leave` / `x-removal` | server logs `removed client=.. reason=disconnect` for X within 25 s |
| `x-removal-late` | graceful only: removal within 2500 ms (later = the server never heard the Disconnect packet) |
| `a-acked` | for 8 s after the removal A's ack (from its `[client] predict .. ack=N` lines) rises by >= 96 and by >= 20 in the last 3 s, A does not exit |
| `z-connect` | a NEW client Z is admitted by the same server within 45 s |
| `z-removal`, `z-removal-late` | Z quits gracefully, removed within 2500 ms |
| `a-acked-after-z` | 5 s after Z's removal A's ack rises by >= 40 and >= 10 in the last 3 s |

A trial that fails ends its session (the fault is permanent); the next trial starts a new session. The first failing stage is the trial's `stage`.

### Reading the output

```
[Emberdelve][UtpProbe] trial arm=baseline n=3 kind=graceful result=FAIL stage=a-acked xRemovalMs=3900 zRemovalMs=-1 acksAfterX=412->430 acksAfterZ=- server-rx-quiet=2 detail=[...]
[Emberdelve][UtpProbe] arm=baseline fix=False lingerMs=0 trials=8 ok=5 fail=3 rate=37%
[Emberdelve][UtpProbe] arm=full fix=True lingerMs=250 trials=8 ok=8 fail=0 rate=0%
[Emberdelve][UtpProbe] verdict baseline-repro=yes mitigations: full=clean-effective
[Emberdelve][UtpProbe] PASS checks=.. failures=0
```

- Final line: PASS means infrastructure worked AND every trial of every non-baseline arm passed. The baseline arm is evidence and never adds a failure. The runner's regex reads this line.
- `xRemovalMs` far above 2500 on a graceful trial = the server did not receive the Disconnect: the receive path was already dead (the `net-final` signature).
- `acksAfterX=a->b` shows how far A's ack moved; a stall at the same number is the `net-admission` signature.
- `server-rx-quiet` counts the server's own `rx-quiet` diagnostic lines in that session.
- `logscan` lines print the first few lines containing `exception|error|transport failure|connreset` found in the server and A logs after a failed trial (the CI logs had none; if the fix arm ever has one, that is the first thing to read).
- `verdict`: `baseline-repro=yes` and a mitigation arm `clean-effective` proves the fault is the transport/OS reset and that the arm fixes it. `baseline-repro=no` means the fault did not appear in N trials: the A/B is inconclusive, not a pass of the mitigation (raise `-ember-probe-trials`). `still-failing` / `-ineffective` on an arm with the fix applied means the hypothesis (or the ioctl on that socket) is wrong.
- Fix arms also check the server log for `udp-connreset disabled sockets=`; if the line says `NOT applied`, the check fails by name, because that arm then tests nothing.
- To separate the two mitigations run `-ember-probe-arms baseline,a,b`.

Per-trial logs are in `<run>/logs/<arm>-s<session>-{server,a}.log` and `<arm>-s<session>-t<n>-{x,z}.log`.

## 6. Left open

- Everything depends on a Windows run of the probe; nothing here was compiled or executed.
- Only the server socket is touched. The clients' own sockets also see ICMP when the server dies; not covered because the observed fault is on the server.
- If the probe shows the baseline failing and `a` failing too, the next candidates are (1) raise the transport log level on the server and look for the receive-job failure, (2) have the server disconnect a peer that has been quiet for N ticks (`DisconnectClient`) before the transport timeout, (3) a watchdog that rebinds the transport when every peer goes quiet at once and the bound port received nothing for N seconds.


## 8. Round 1 results (Windows, probe run 20261028-162941)

Per fresh server session, trials alternate graceful / hard (odd = graceful, even = hard):

| trial | 1 graceful | 2 hard | 3 graceful | 4 hard |
|---|---|---|---|---|
| baseline s1 | OK | OK | OK | FAIL (A dropped with code 13, new client never admitted) |
| baseline s2 (trials 5-8) | OK | OK | OK | FAIL (z-connect) |
| full arm (fix + linger 250) | identical: 4 and 8 FAIL | | | |

- Deterministic: in every session the SECOND hard kill (4th departure) permanently kills the server's receive path. The first hard kill and all graceful leaves are fine. Hard removal takes ~4000-4200 ms (the timeout), graceful ~200-450 ms.
- The `full` arm proves nothing about the hypothesis: the server logged `udp-connreset NOT applied: no UDP socket bound to port 62352 found in this process`, detail `reflect-driver=found reflect-candidates=0 reflect-verified=0 probe=timeout scanned<=65536`.
- Reading the pattern (hypothesis, not fact): "second hard kill, never the first" and "never a graceful one" is not what a pure per-datagram ICMP race looks like (that would be random). Something counts. Candidates: (1) the first hard kill's ICMP is absorbed/ignored and the second is not (a state change after the first reset); (2) a resource that leaks on a timed-out (not disconnected) connection and runs out on the second one, for example a connection / endpoint slot of the transport (max players is 3, so with A, X and Z the table is small); (3) Windows rate-limits or suppresses ICMP, which changes WHEN the reset surfaces, not whether. The probe cannot separate these from the outside; the arms below can: if `scan` (reset off) fixes it, (1)/(3) were right; if `scan` is applied (the log says so) and still fails, it is (2) or something in baselib, and `managed` / `ws` (which replace the socket layer) are the real test.

## 9. Round 2: socket paths

Switch: `-ember-net-udp-path stock|scan|managed|ws`, honoured by EVERY role (server, host, client), default **scan**. The old `-ember-net-udp-reset-fix false` still works and means `stock` unless a path is given explicitly. The first thing every role logs is `socket-path <path> requested=.. effective=.. transport=..`; `effective=stock-FALLBACK reason=..` means the requested path could not be installed and the process runs on the stock socket (the probe fails that arm by name).

### (A) scan (default): find the socket, switch SIO_UDP_CONNRESET off  -  `NetUdpResetGuard`

Why round 1 found nothing: the blind scan walked handle values 4..65532 on a worker thread with a 3 s cap, and the cap fired (`probe=timeout`) before it finished, so even a hit would have been discarded; it also could not see handle values above 65535.

Now (background thread, no cap on the scan itself, 15 s budget for the whole attempt, rounds every 500 ms):
1. `NtQuerySystemInformation(SystemExtendedHandleInformation = 0x40)` lists every handle in the system; the entries with our PID are kept (any handle value).
2. The object type index of a socket is learned from a throw-away managed UDP socket created in the same call; only handles of that type are probed. The sockets' usual access mask (0x001F01FF) goes first. Handles with access masks that are known to block name queries on synchronous pipes (0x0012019F, 0x001A019F, 0x00120189, 0x00100000) are never touched. Probing uses 4 threads, so one blocking handle cannot stall the scan; a stall of 10 s without progress abandons the stuck threads.
3. Per candidate: `getsockopt(SO_TYPE) == SOCK_DGRAM`, `getsockname` (port), then `WSAIoctl(SIO_UDP_CONNRESET, FALSE)` and `WSAIoctl(SIO_UDP_NETRESET, FALSE)` on EVERY datagram socket of the process (harmless on others). Server role succeeds when a socket on the server port was changed; a client (ephemeral port) when any datagram socket was.
4. If the table cannot be read, it falls back to the old blind 4..65532 range, still on the background thread.
Runs on the server (after `listening`) and on clients (right after `StartClient`). Log lines (prefix `[Emberdelve][Net][server|client] `): one `udp-connreset scan round=N system-handles=.. own=.. file-type-index=.. candidates=.. skipped-hangmask=.. probed=.. sockets=.. stalled=.. udp=[handle:port:connreset=off:netreset=off(:SERVER),..] probe-errors=[wsaNxM,..] elapsedMs=..` per round, then `udp-connreset disabled sockets=N port=P role=..` or `udp-connreset NOT applied: ..`, then `udp-connreset detail ..`. If this fixes the fault, the WSAECONNRESET hypothesis is proven.

What to read if it still says NOT applied: `own=` 0 = the table could not be read; `file-type-index=-1` = reference socket not found; `probe-errors=[wsa10038xN]` = N handles were not sockets in ws2_32's eyes; `sockets=0` with many `wsa10038` = ws2_32 does not recognise baselib's socket handle at all (then `scan` cannot work and `managed` is the way). If `udp=[..]` lists the server port but `connreset=errNNN`, the ioctl was refused: NNN is the WSA error.

### (B) managed: own INetworkInterface over System.Net.Sockets  -  `Assets/Emberdelve/Net.Managed/` (OPTIONAL assembly)

Compiled only when the scripting define `EMBER_UTP_MANAGED_SOCKET` is set (menu `Emberdelve/Net/Toggle managed UDP socket define`, or batch `-executeMethod Emberdelve.EditorTools.EmberdelveManagedSocketDefine.Enable`; Unity then recompiles). It is opt-in because it is written against the Unity Transport 2.x / NGO 2.x API from memory and could not be compiled here: an API mismatch must not break the project or the default path. `Emberdelve.Net` reaches it by reflection (`NetSocketPath`).
- `ManagedUdpDriver : INetworkStreamDriverConstructor` builds the `NetworkDriver` with `ManagedUdpInterface` (an unmanaged struct holding an id; the `Socket` lives in a static table because `NetworkDriver.Create<N>` needs an unmanaged `N`) and the three pipelines (unreliable+fragmentation, unreliable-sequenced+fragmentation, reliable-sequenced with fragmentation in front).
- Socket: non-blocking `Socket`, 1 MiB buffers, `IOControl(SIO_UDP_CONNRESET = -1744830452, {0,0,0,0})` and SIO_UDP_NETRESET off on Windows BEFORE bind; a `ConnectionReset` that still surfaces on receive is counted and swallowed instead of ending the loop. Receive and send run synchronously inside `ScheduleReceive` / `ScheduleSend` after `dep.Complete()` (managed sockets cannot run in a Burst job), which blocks the main thread only for the length of a socket read/write (loopback: microseconds). Max 512 datagrams per receive call; IPv4 only.
- Log: `socket-path managed detail managed-udp bound=127.0.0.1:62352 connreset=off netreset=off rx=.. tx=.. connreset-swallowed=.. rx-errors=.. tx-errors=.. rx-queue-full=..` after `listening` and at every client disconnect. `connreset-swallowed > 0` after a hard kill would be direct evidence that the ICMP did arrive.
- Hook: reflection looks for the instance property `UnityTransport.DriverConstructor` (setter) and then the static field `s_DriverConstructor`; neither found = FALLBACK to stock with the reason in the log.

### (C) ws: WebSockets

`UnityTransport.UseWebSockets = true` on every role (TCP, no ICMP). Behind `-ember-net-udp-path ws`; not the default because TCP head-of-line blocking changes the latency behaviour of a movement game. It is the control that says whether the fault lives in the UDP socket layer at all: a hard kill gives the server a TCP reset instead.

### Default and why

Default = **scan**: it keeps the package's own transport (no new code in the data path, no behaviour change except one socket option) and it is the only option whose cost, if wrong, is zero. It is also the cheapest test of the hypothesis. If the probe shows `scan` applied and still failing, change the default to `managed` (if it passes) else `ws`. The probe prints `recommend default-path=..` (first clean arm in the order scan, managed, ws, while the baseline reproduces the fault). Client linger (250 ms) stays as it is; it is documented above as unlikely to matter.

### Probe

`Emberdelve.EditorTools.EmberdelveUtpResetProbe.Run`, default arms `baseline,scan,managed,ws`, 8 trials each (graceful, hard alternating; the baseline is expected to fail at trial 4 and 8). Others: `a` (= scan), `b` (stock + linger 250), `full` (scan + linger 250). Each session logs `session <stem> socket-path: <the server's socket-path line>` and Checks that `effective=<arm path>`; a `scan` session also Checks `udp-connreset disabled sockets=` (waits up to 20 s for the background scan), a `managed` session Checks the bind line. The `managed` arm is SKIPPED with a warning (not a failure, no evidence) when the assembly is not compiled; enable the define, let Unity recompile, run again. Runtime ~35 s/trial: 4 arms x 8 trials is about 20 minutes plus the build; give the runner `-TimeoutSeconds 3600`.

### Uncertainties (round 2)

- (A) That ws2_32 recognises baselib's socket handle (`getsockopt` works on it) and honours the ioctl on a Registered-I/O socket. Unverified. That GetFileType-free probing of "File" objects with the listed masks never blocks; unverified, mitigated by 4 threads and the stall limit. That the sockets' type index equals the managed reference socket's (both are AFD file objects): very likely, logged as `file-type-index`.
- (B) Everything API-shaped: `INetworkStreamDriverConstructor` being public and its exact `CreateDriver` signature; the `DriverConstructor` / `s_DriverConstructor` hook; `NetworkDriver.Create<N>(N, NetworkSettings)`; `WithNetworkConfigParameters(connectTimeoutMS, maxConnectAttempts, disconnectTimeoutMS, heartbeatTimeoutMS)` parameter names; `WithFragmentationStageParameters(payloadCapacity)`; `WithReliableStageParameters(windowSize)`; `PacketsQueue` (`Count`, `Capacity`, indexer, `EnqueuePacket(out PacketProcessor)`); `PacketProcessor` (`Capacity`, `Length`, `GetUnsafePayloadPtr()`, `EndpointRef`, `SetUnsafeMetadata(int)`, `DropPacket()`); `ReceiveJobArguments.ReceiveQueue`, `SendJobArguments.SendQueue`; `NetworkEndpoint.TryParse(string, ushort, out)`, `ToString()` giving `a.b.c.d:port`, `.Port`. Each is marked `API?` in the source. Also assumed: the driver clears the send queue itself (if it does not, packets are resent every update and the log will show huge `tx`), and the pipeline set is the one NGO's default constructor builds (if NGO expects different pipelines the symptoms are send failures or messages larger than a datagram dropped).
- (C) That NGO 2.7's WebSocket mode on a standalone server/client (not WebGL) works with `SetConnectionData(listen, port, listen)` unchanged; expected from the package docs, unverified.
- The "second hard kill" pattern (section 8) has no explanation yet; if all of scan / managed / ws fail on the same trial, the cause is above the socket layer (connection bookkeeping in NGO/UTP), and the next step is the transport log level on the server and a `DisconnectClient` of silent peers before the timeout.
