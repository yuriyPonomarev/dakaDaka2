# Network play: Squire Royale and development proofs

## Current Royale mode (13 November 2026)

The playable mode uses three separate maps across moonlit ruins, enclosed crypts and infernal halls. Choose SOLO, DUO or TRIO in the lobby. These modes use 16, 16 and 15 participants respectively. Empty seats and destination refill use server-owned bots. Clients and authority must use the same build (current protocol 45).

For offline play, select a class and mode in the lobby, prepare the raid and launch. Each raid rolls safe positions for the red descent portals on floors 1 and 2. Three portal groups open during the floor, with the last group appearing in the final minute. Approach an available slot and press F. In offline SOLO, entering the portal immediately starts the transition. Any bots already in transit join the destination as it loads.

In DUO and TRIO, hold F at a red portal to enter your slot. Releasing F, moving or taking damage interrupts activation. After entering, you watch living allies on that floor from third person. Press Tab to switch between allies. The remaining slots in the portal group are reserved for their party. Once every teammate has entered or died, the players who entered descend together with their equipment and loot. Online, their destination maps are checked before the party arrives.

After dying in DUO or TRIO, you can watch living allies from third person as they continue to later floors. Tab switches allies. Results opens your own raid result, and Spectate team resumes the view while any ally is still playing. Your result opens automatically once all allies have died, extracted or disconnected.

Chests and fallen adventurers use the paired loot and inventory screen. On floor 3, hold F continuously at the final gate to bank the loot. Movement, damage or releasing F interrupts the hold. Offline extraction saves the returned equipment and carried items. Death loses the raid kit and stash items remain safe.

Command-line host/server selection adds `-ember-royale-mode solo`, `duo` or `trio` to the commands below. The Royale roster owns its16/16/15 capacity; the legacy `-ember-net-max-players` option below is used by development non-Royale proofs. Networking remains opt-in through the lobby Online panel or `-ember-net ...`; one executable supports offline, host, client and dedicated server.

Windows standalone builds currently default to Direct3D11 for compatibility with the pinned Unity version after an observed DX12 device-loss incident. This is an API selection workaround, not an engine upgrade or a claimed DX12 fix.

## 1. Build and validate

Keep the user's main Unity Editor open and untouched. Run tests/builds only on the CI mirror through `Tools/Run-All.ps1`; do not launch a second executor against that mirror. Use the scoped verification matrix in AGENTS.md. Builds are copied back to `Builds/Playable/<run-id>/EmberdelveNet.exe`; results are in `Logs/runs/<run-id>/run.json` and `summary.txt`. A build success alone does not mean its gameplay checks or visual acceptance passed.

The command-line examples below describe development connection/lifecycle tooling. Historical U3A proof limits are not a current Royale feature-completion claim; CURRENT_WORK.md and Tasks/SQUIRE_ROYALE_20261113.md own acceptance status.

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

`Tools\net\Start-Host.bat [slot] [class]`, or the HOST button in the lobby (listens on 127.0.0.1 and the port in the address field; type `0.0.0.0:7777` to listen on the LAN). The host is a server plus your own player in the same window, using the same admission code as a remote player. Royale host/client transitions are covered by NetRoyale; the separate NetFinal lifecycle gate uses a dedicated server. Escape stops the host and returns to the lobby (everyone connected is disconnected).

## 5. Legacy non-Royale lifecycle proof rules

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

Q/E activate abilities; 1/2 select weapon sets. Hold 3 or 4 to open a three-slot quick wheel, point at an item, then release to take it in hand and stow weapons. LMB uses the held item. Ammo is the exception: selecting arrows/bolts in a wheel or right-clicking them replenishes the matching drawn ranged weapon without stowing it; full reserves or the wrong weapon consume nothing. Potions still require holding. In the inventory, hover or select an item and hold 3/4 to assign a wheel slot; right-click takes a utility item in hand. F interacts with containers, doors and lights. I opens the inventory.
