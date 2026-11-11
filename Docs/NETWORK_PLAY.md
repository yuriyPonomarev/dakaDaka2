# Network play (U3A): how to start a server and clients

The game always uses the authored Modular Crypts map for offline play, Host, and dedicated servers. Clients join the server-selected scene and plan. Server-simulated movement, owner prediction, interpolated other players, late join, clean disconnect and rejoin are supported. Combat, inventory, chest and enemy presentation remain incomplete online; proofs usually run with `-ember-net-no-enemies`.

Everything below uses ONE executable. Networking is chosen by the command line (`-ember-net ...`) or by the lobby's Online panel; without either the process is the plain offline game.

## 1. Build the player

Close the Unity Editor for this project, then run `Tools\net\Build-NetPlayable.bat` (or the same command by hand):

```
"C:\Program Files\Unity\Hub\Editor\6000.2.5f1\Editor\Unity.exe" -batchmode -nographics -quit -projectPath <project> -logFile build_net.log ^
  -executeMethod Emberdelve.EditorTools.EmberdelveWindowsBuild.BuildNetPlayable --ember-build-output C:\Games\EmberdelveNet\EmberdelveNet.exe
```

It requires and builds scenes Lobby + Modular Crypts (Development build), and generates the network player prefab from the modular scene if it is missing. The output must be an absolute path outside `Assets`. Exit code 0 means built. From a running Editor use `EmberdelveWindowsBuild.BuildNetFinalPlayer` (internal) or the gate below.

## 2. Dedicated server

`Tools\net\Start-Server.bat [exe]`, or:

```
EmberdelveNet.exe -batchmode -nographics -logFile server.log -ember-net server -ember-port 7777 -ember-net-max-players 9 -ember-net-quit-file server.quit
```

- Listens on 127.0.0.1 only. `-ember-listen any` binds 0.0.0.0 (LAN); Windows Defender Firewall will ask for permission the first time, and an unattended run blocks on that dialog. Use it only when you want other PCs to join.
- Ready when `server.log` shows `[Emberdelve][Net][server] ready scene=... players=0`. Then it writes a status line every two seconds (`status players=N senders=N registry=N`).
- Stop it cleanly with `Tools\net\Stop-Server.bat` (creates `server.quit`; exit code 0, log ends `shutdown players=0 senders=0 registry=0`). Closing the process also works: clients see a disconnect.
- Useful switches: `-ember-net-no-enemies`, `-ember-net-autoquit <seconds>`, `-ember-net-disconnect-timeout-ms <ms>` (how fast a dead client is noticed; default is the transport's 30 s).
- Windows UDP reset mitigations (see `Docs/Tasks/UTP_RESET.md`): `-ember-net-udp-path stock|scan|managed|ws` (all roles, default `scan`: find the transport socket and turn off SIO_UDP_CONNRESET / SIO_UDP_NETRESET; `managed` = custom socket interface, needs the `EMBER_UTP_MANAGED_SOCKET` define; `ws` = WebSockets; legacy `-ember-net-udp-reset-fix false` = `stock`) and `-ember-net-client-linger-ms <ms>` (client, default 250: wait after a requested shutdown before the process quits; 0 = off).

## 3. Clients

From the command line (straight into the dungeon, no lobby): `Tools\net\Start-Client.bat [server IPv4] [slot] [warrior|archer]`, or:

```
EmberdelveNet.exe -screen-width 1280 -screen-height 720 -logFile client.log -ember-net client -ember-address 127.0.0.1 -ember-port 7777 -ember-slot player1 -ember-class warrior
```

From the lobby (Online panel, top left): type the server as `IPv4:port` (default `127.0.0.1:7777`, a bare IP uses port 7777; click the field, Ctrl+V pastes), pick a class card, press JOIN. The status line shows `connecting...`, then `connected ... (Esc leaves)`, or `refused: <reason>` (`server full`, `duplicate slot`, `unknown class`, `protocol mismatch ...`), or `no answer from ... (timeout)`. CANCEL aborts a connection attempt. Escape in the dungeon leaves the server and returns to the lobby with `left the game`; a lost server shows `disconnected from the server`. Join uses exactly the code the command line uses (same bootstrap, scene sync from the server). Names are not resolved: use dotted IPv4 (or `localhost`).

The slot is a development identity, NOT a login: the server uses it only to refuse a second connection with the same slot. The lobby generates a random slot per game session (`p` + 8 hex digits) and keeps it while the game stays open, so leaving and joining again is a rejoin under the same slot. A command-line `-ember-slot` overrides it.

Class: the id string only (`warrior`, `archer`); the server resolves everything else. An unknown id is refused.

## 4. Host (experimental)

`Tools\net\Start-Host.bat [slot] [class]`, or the HOST button in the lobby (listens on 127.0.0.1 and the port in the address field; type `0.0.0.0:7777` to listen on the LAN). The host is a server plus your own player in the same window, using the same admission code as a remote player. It is NOT covered by the process gate (the gate runs a dedicated server); treat it as a development convenience. Escape stops the host and returns to the lobby (everyone connected is disconnected).

## 5. Lifecycle rules (decisions)

- Late join: allowed while the run is Running. A new client gets every existing player at the server's current position, unique ids, and spawns on the first layout spawn nobody stands within 1.2 m of.
- Disconnect removes the actor immediately (graceful quit and hard kill alike): session, party seat, combat entity, sender binding and the network object are gone; no corpse, no linger. A killed process is noticed after the transport timeout.
- Rejoin: the same slot connecting again after its previous actor was removed is admitted as a NEW actor with a NEW player id; nothing carries over (server logs `rejoin slot=... newId=... previousId=...`). While the old connection is still being timed out the slot is refused as `duplicate slot`.
- Capacity: `-ember-net-max-players N` (default 9). A full server refuses with `server full`; a seat frees as soon as the disconnect is processed.

## 6. Exit codes and logs

0 clean; 2 bad arguments; 10 start failed; 11 could not connect or refused; 12 transport failure; 13 connection lost; 14 shutdown timeout. All network lines start with `[Emberdelve][Net][server|client|host]`. Lobby lines start with `[Emberdelve][Lobby]`.

## 7. Proof gates (batch Editor, `Tools\Run-EmberdelveGates.ps1 -Methods ...`)

`Emberdelve.EditorTools.EmberdelveNetFinalGate.Run` is the roadmap acceptance (build, server max-players 3, A+B, walk, cheats, late join through the lobby Join path, full refusal, hard kill, retry, rejoin, graceful quits, offline retention, no leftover processes). The earlier gates stay separate entries: `EmberdelveNetProofGate`, `EmberdelveNetAdmissionGate`, `EmberdelveNetMovementGate`, `EmberdelveNetSeamsGate`, `EmberdelveNetSkillGate` (U3B2: server-owned active skills, abuse control; see `Docs/Tasks/U3B2.md`). Each ends with `PASS|FAIL checks=N failures=M`.

`Emberdelve.EditorTools.EmberdelveUtpResetProbe.Run` is a diagnostic gate, not part of the acceptance set: it loops a leaving client against a staying one and reports whether the dedicated server keeps receiving (A/B of the two UTP reset mitigations). Run it with `-TimeoutSeconds 3600`. How to read it: `Docs/Tasks/UTP_RESET.md`.

## Current player controls

Q/E activate abilities; 1/2 select weapon sets. Hold 3 or 4 to open a three-slot quick wheel, point at an item, then release to take it in hand and stow weapons. LMB uses the held item; items cannot be consumed directly from the bag. In the inventory, hover or select an item and hold 3/4 to assign a wheel slot; right-click takes a utility item in hand. F interacts with containers, doors and lights. I opens the inventory.
