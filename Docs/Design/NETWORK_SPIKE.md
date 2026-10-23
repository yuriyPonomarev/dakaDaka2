# U3 network spike — implementation constraints

Research/planning only, 23 October 2026. No networking package has been installed and no online acceptance is claimed.

## Requirements already decided

Windows first; server-authoritative competitive PvPvE, 6–9 players in parties of up to three. First acceptance is a dedicated simulation process and two independently built client processes. Preserve the offline route. The initial local/LAN proof must not depend on paid hosting or an external matchmaking deployment.

## Candidate stack and verification

Use the existing GameObject/MonoBehaviour game with Netcode for GameObjects and its Unity Transport dependency. Unity documents NGO 2.7 support for Unity 6.0 and later; the actual selected package/version and transitive dependency changes must be checked through PackageManager before installation. Keep Unity 6000.2.5f1 and URP unchanged.

- Official package documentation: https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.7/manual/index.html
- Official session networking overview: https://docs.unity.com/en-us/mps-sdk/session-networking
- Official session integration guide: https://docs.unity.com/en-us/mps-sdk/tutorials/build-your-first-session

The project has a cloud project association, which does not prove service initialization/authentication or deployment readiness. The first dedicated/local connection proof can use direct transport. Internet discovery and managed session lifecycle should use Unity.Services.Multiplayer primary session APIs when added; do not introduce separate lower-level service clients by default. Validate prerequisites before promising internet play.

## Boundaries to preserve

- The receiving server derives actor identity from the authenticated connection binding. A payload cannot choose another player ID, class asset, weapon, stats, damage, item roll, result, or profile path.
- Existing LocalCombatAuthority is local gameplay resolution, not sender authentication. Network adapters must call it only on the simulation server; clients submit inputs and render results.
- Server movement consumes validated input/state and runs collision/ability movement. A client transform or client-finished Dash is not an authoritative position.
- Per-player views and inputs are separate from simulation authority: a remote server actor must still simulate without a local camera or keyboard.
- Client inventory operations carry stable stack/gear identity, sequence and revision; authoritative ownership and atomic commits stay on the server.
- Profiles identify a durable player slot, not the transient peer connection. Development loopback identity is explicitly scoped and is not production authentication.

## Small implementation slices

1. Verify package metadata and dependency delta; install only selected networking dependencies through UPM Client API and compile.
2. Saved network bootstrap/player prefab, command-line server/client/offline roles, admission cap and connection-to-actor mapping.
3. Server movement and client interpolation/ownership, two independent clients; unauthorized/replayed movement and cleanup.
4. Late join and disconnect/rejoin lifecycle with unique actors; preserve offline play and expose a usable join flow.
5. Server combat/resources/skills and rendered agreement on actual trajectories.
6. Contested chest/inventory, death/extraction and profile persistence after the relevant local contracts are complete.

Record logs and actual moving client screenshots per slice. A local multi-actor harness, one process hosting two objects, a connected status alone, or a rendered frozen frame is not this network proof.
