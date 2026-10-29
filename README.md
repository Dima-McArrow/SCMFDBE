# CSMFD Server (Windows BE)

Minimal, secured Windows companion for the CSMFD iPad MFD (Star Citizen). It accepts a small allowlist of
commands from **one paired iPad**, injects them into the focused game with `SendInput`, reads new
`Game.log` lines and publishes a compact state.

The [SCMFD iPad app](https://github.com/Dima-McArrow/SCMFD) (SwiftUI) is a separate repository, usually checked out
next to this one. Contract: **protocol v2**, section 41 of the
[shared specification](https://github.com/Dima-McArrow/SCMFD/blob/main/Claude%20outputs/CSMFD-discussion.md)
(`..\SCMFD\Claude outputs\CSMFD-discussion.md` locally). Section 40 (v1) is historical and
not implemented. Agent and contributor rules: [`CLAUDE.md`](CLAUDE.md); design report (Russian):
[`docs/SERVER-IMPLEMENTATION.md`](docs/SERVER-IMPLEMENTATION.md).

What it deliberately does **not** do: no web UI, no REST, no cloud/accounts/analytics, no screen capture/OCR,
no memory reading, no DLL injection or driver, no arbitrary macros or key names, no auto-update, no admin rights.

## Two repositories, one companion

| Repository | Role | Runs on |
|---|---|---|
| [SCMFD](https://github.com/Dima-McArrow/SCMFD) | Touch controls, themes, observed activity and local history | iPad |
| [SCMFDBE](https://github.com/Dima-McArrow/SCMFDBE) (this repository) | Game input, log observations, pairing and secure transport | Windows gaming PC |

For connected use, build this server and the [iPad app](https://github.com/Dima-McArrow/SCMFD#build-and-test).
Put both devices on the same private network, follow [First start](#first-start), then use **Connect to PC**
in the app. Update both sides together when the shared protocol changes.

## Why SCMFD?

**Touch controls and log-driven information, together on a native iPad MFD.** SCMFD combines a cockpit-style
control surface with feedback from the events Star Citizen exposes in `Game.log`. The aim is to make useful
log observations visible beside the controls you use, and expand that feedback as more event formats are verified.

- **A cockpit that follows your ship.** With Auto theme enabled, a recognized ship-channel join switches the
  interface to the ship manufacturer's theme. Leaving the ship channel returns it to the default SCMFD theme.
  The 18 manufacturer themes bring their own palettes, fonts, key shapes and HUD details; manual selection
  remains available.
- **Your selected quantum destination beside Engage.** When a starmap selection is reported in the log, the
  Quantum page shows its target name directly above the Engage key. Target names can still be technical names;
  the app displays what the server actually observes.
- **Feedback beyond button presses.** Landing requests show pending, answered or timeout status. Observed
  contracts, objectives and rewards populate the session pages, while the read-only Party tab shows observed
  members, the last known leader and membership events, with explicit uncertainty when information is incomplete.
- **Native touch interaction.** SwiftUI controls, timed holds, a slide-to-arm for self-destruct, interface sounds
  and English/French/Russian text make the iPad a dedicated companion display.
- **A direct local connection.** The iPad talks to its Windows companion over the private network. Normal
  operation requires no project account, cloud service, AI provider or subscription. The PC reuses one log reader;
  presentation and history belong to the iPad.

Input is restricted to an allowlist, checked against game focus and physical-key conflicts, with release paths
for disconnects and other safety events. Log-derived information is partial; current validation and remaining
limits are documented below. No game-memory access or modification is used.

### Respect to fellow companion projects

Much respect to [GameGlass](https://gameglass.gg/) for its touchscreen game-control panels and to
[Wingman AI](https://www.wingman-ai.com/) for showing how Star Citizen's game log can inform a companion's
responses to game events. SCMFD brings these two ideas together in its own form: a native iPad touch interface
and visual feedback driven by verified `Game.log` observations. It has no voice assistant; the shared idea with
Wingman AI here is reading the game log. Thanks to both teams and their communities for advancing companion
experiences in the 'verse.

## License

**Source-available. Free for noncommercial use.**

Original project code is licensed under [PolyForm Noncommercial 1.0.0](LICENSE).
You may use, study, modify and share it for the purposes permitted by that license,
while retaining its terms and the [copyright notice](NOTICE). Copyright remains
with the respective rights holders.

[Additional permission](ADDITIONAL-PERMISSIONS.md) allows using and showing SCMFD
in monetized gameplay streams, videos, tutorials and reviews. Commercial sale of
the app or forks, incorporation into commercial products, and paid services based
on the software require separate written permission unless already permitted by
the license. This is a noncommercial source-available project, rather than an
OSI-defined open-source release.

Third-party components, bundled fonts and game materials retain their own licenses
and rights; the project license does not replace those terms. For commercial
licensing requests, contact [Dima-McArrow](https://github.com/Dima-McArrow).

## Requirements

- Windows 10/11 x64, .NET 10 SDK to build (`global.json` pins 10.0.403, latest feature band allowed).
- The iPad and the PC on the same private network (no guest/client isolation).

## Build, test, run

```powershell
dotnet build Csmfd.slnx -c Release          # analyzers + code style are errors
dotnet test  Csmfd.slnx -c Release          # unit, Win32-pure and loopback WSS end-to-end tests
dotnet format Csmfd.slnx --verify-no-changes
dotnet publish src/Csmfd.Server -c Release -o publish
publish\CSMFD.Server.exe                    # tray app, ordinary user
```

`CSMFD_DATA_DIR=<folder>` redirects all data (config, certificate, trust, logs) for development runs.

## First start

1. Tray icon → **Network** → choose the Wi-Fi/Ethernet private IPv4 the iPad should use.
   Nothing is chosen automatically; VPN-like adapters are flagged. The listener binds to that address only
   (`wss://<ip>:47823/csmfd`, TLS 1.2+). If the address disappears the listener stops and input is released.
2. Tray → **Copy firewall command**, run it once in an **administrator** terminal. It allows TCP 47823 for
   this exe, Private profile, local subnet only. Never disable the firewall or forward the port on the router.
3. Tray → **Pair iPad…** (opens a 120 s window). The iPad scans the QR code (or compares the fingerprint),
   verifies the pinned certificate, then sends the 6-digit code. 5 wrong codes in total close the window.

QR payload (no secret inside):
`csmfd://pair?v=2&host=<ip>&port=47823&id=<serverId>&fp=<64 hex SHA-256 of the DER certificate>`

mDNS: `_csmfd._tcp`, TXT `v=2`, `tls=1`, `id=<serverId>`, `name=<display name>` on the chosen interface only.
Discovery is a convenience; the fingerprint is never taken from mDNS and manual IP entry always works.

## Tray

Status · Pair iPad · Network · Game (choose `StarCitizen.exe` of LIVE, PTU…; `Game.log` is derived from it; applies
after a restart) · Trusted devices (revoke one / all) · Pause input (releases immediately) ·
Re-enable input (only after a failed release) · Input monitor · Debug logging (15 min, then off by itself) ·
Copy firewall command · Open data folder · Quit.

**Debug logging**: for 15 minutes the log file also explains Game.log decisions (recognized event and offset, ship
context revisions, ship ↔ entity binding, local QT selections, ignored ClearDriver / hangar notifications and why, ATC pending /
answered / timeout) and every state sent to the iPad (seq, full or delta). Same privacy rules as the normal log: no
player handle, raw log lines, chat text, tokens or codes. Always off after a restart.

**Input monitor** (debug window): every command received from the iPad, its result (sent / rejected with reason /
ignored, server-side releases) and the exact events handed to SendInput (e.g. `Alt↓ N↓ … N↑ Alt↑`). It records only while
the window is open, keeps at most 2000 pending entries in memory, never writes to disk and is not reachable over the network.

## Data (`%LocalAppData%\CSMFD`, current user only)

| File | Content |
|---|---|
| `config.json` | `bindAddress`, `gameExePath`, `gameLogPath`, `chatKey` (`enter`/`f12`), `displayName`, `serverId` |
| `server-certificate.bin` | ECDSA P-256 certificate + key, PKCS#12 protected with DPAPI (CurrentUser). Created once; deleting it requires re-pairing |
| `trusted-devices.json` | Device id, name, pairing time and **SHA-256 of the token** (never the token) |
| `emotes.txt` | Chat allowlist, one base name per line (`/name` and `/name<N>` accepted). **Sync with `Emote.swift`** |
| `logs\csmfd*.log` | Lifecycle and warnings (plus decisions while Debug logging is on), 3 × 1 MiB. No tokens, codes, keys or Game.log content |

## How it maps to the spec

| Area | Implementation |
|---|---|
| Wire contract (41.6–41.10) | `Csmfd.Protocol`: strict parser (duplicates, depth 8, 16 KiB, UUIDs, allowlist), writer enforcing state invariants, JSON fixtures in `tests/Csmfd.Tests/Fixtures/Protocol` |
| Transport (41.6, 41.7) | `ConnectionSession`: one receive + one send loop, 1003/1007/1009, hello 5 s, heartbeat 15 s, >100 msg/s → 4005, bounded queues, 2 s send deadline, coalesced state (20 ms, ship-context transitions urgent) |
| Pairing & trust (41.5) | `PairingService`, `ServerCertificateStore`, `FileTrustStore`; replacement releases old input before the new client acts; revoke closes 4006 |
| Input (41.11, 41.12) | `InputExecutor`: one gesture at a time (`busy`), 60 ms tap minimum without blocking, hold deadlines with stale-timer guard, 10 s ceiling, focus/process + physical modifier checks right before input, safety release on focus loss, lock, game exit, ClearDriver, disconnect, pause, shutdown; chat sequence never presses Enter outside the game |
| Win32 | `SendInput` scan codes / MOUSEINPUT / KEYEVENTF_UNICODE, image-path focus check, WinEvent foreground hook, no keyboard polling |
| Game.log (41.13, 41.14) | `LogTailer` (EOF start, byte offsets, generations), `GameLogParser` (verified lines of build 12913470), `GameLogProcessor` (ship context, QT target, ATC with watermark) |

## Measured on the gaming PC (2026-10-27, game not running)

Release build, `CSMFD_DATA_DIR` dev instance, LAN 192.168.1.67, client = `tools/csmfd-probe`.
Targets from spec 41.15 in parentheses. CPU is "% of one logical core" (CPU time / wall time).

| Metric | Result |
|---|---|
| Idle CPU, warm (after 3 min), 120 s | 0.052% (< 0.5%) |
| Idle CPU, first 2 min after start | ~0.8%: one-time .NET tiered JIT warm-up |
| Idle CPU with a connected client pinging every 5 s | 0.68% measured during warm-up, same order as above |
| Private bytes | 23–27 MiB (< 100 MiB), stable after reconnects and a 117 MiB log burst |
| Log burst: real 117 MiB archive appended at once | whole server used ~1.3 CPU-s; parsed to the end, `ready`, 16 context transitions |
| Ping RTT over LAN | 1.6–2.9 ms |
| Command → ack (rejected path, includes process check) | 13 ms |

Not yet measured: in-game frametime impact, real `SendInput` latency into the game, 2 h soak.

Dev client:

```powershell
csmfd-probe --host <ip> --print-fp                               # read the fingerprint (connection refused)
csmfd-probe --host <ip> --fp <hex> --code <6 digits>             # pair (prints the token)
csmfd-probe --host <ip> --fp <hex> --token <token> --action lights  # one allowlisted command
csmfd-probe --host <ip> --fp <hex> --token <token> --stay 120       # idle client, ping every 5 s
```

## Repository layout

| Folder | Content |
|---|---|
| `src/Csmfd.Protocol` | Wire contract v2: strict parser, state writer, action allowlist, key names, limits. No dependencies |
| `src/Csmfd.Core` | Platform-neutral logic: input executor, command gate, connection session, pairing, state store, Game.log pipeline, input monitor |
| `src/Csmfd.Server` | Windows host: composition, Kestrel/WSS, config and storage, certificate and trust, Win32 input/focus/mDNS, tray |
| `tests/Csmfd.Tests` | xUnit: protocol, core (fake clock and input), Win32-pure checks, loopback WSS end-to-end; JSON and Game.log fixtures |
| `tools/Csmfd.Probe` | `csmfd-probe` dev client that plays the iPad |
| `docs` | Design and implementation report |

## Status

| Part | State |
|---|---|
| Observed Party MVP (41.24) | Six notification facts through the existing bounded activity journal; no BE roster/calculations. Requires updated iPad app; Mac and in-game verification pending |
| Protocol v2, WSS/TLS with pinning, pairing, revoke | Implemented and tested; works with the iPad app over the LAN (2026-10-27) |
| Commands from the iPad | Received, validated and shown in the Input monitor; in-game effect still to be verified |
| Game.log: ship channel join/leave, vehicle destroyed, QT target, ATC | Implemented; checked on real logs (join verified live 2026-10-28, destruction replayed on every real log that has one). Drives the iPad Deck and theme through `shipContext` (iPad `FIX-07`) |
| Session rewards/contracts activity feed (41.21) | Implemented; bounded 256-event replay, four-event pages, no BE disk ledger. Build/WSS tests and read-only real-log replay checked; in-game coverage/frametime not measured |
| Ship-channel observations in activity (41.22) | Removed 2026-10-28: ship channels travel only in `state` (spec 41.23) |
| Tray, network choice, pairing window, input monitor, icon | Working on the gaming PC |
| In-game measurements (frametime, input latency, 2 h soak) | Not done |

## Verified ship-name alias (2026-10-29)

The exact observed channel name `Corsair PYAM Exec` maps to Drake (`DRAK`) and the verified model
`DRAK_Corsair_Exec_StealthIndustrial`. Join/leave and existing entity binding now recognize this variant;
arbitrary channels beginning with Corsair are not accepted. No protocol change, new reader, polling or FE
calculation is introduced. This does not change menu-reentry recovery.

## Known limits (honest list)

- The ship context follows the **ship channel** (join, leave of the current ship's channel, destruction, game end).
  Leaving the seat (ClearDriver) only releases held input. The ship is assumed, not a confirmed seat.
- Destruction is seen only with the player aboard (the log has no line otherwise). Storing the ship at a terminal
  is not detected yet: no verified line.
- A remote disconnect from `SC_Default`, or the verified local `DisconnectCmd: disconnect light ExitToMenu`
  with `isRemote=0`, ends the ship context. Frontend channels and other local disconnect reasons are ignored.
  Returning to PU does not itself restore the ship context: it stays ended until the next recognized ship-channel
  join. Menu exit does not end the Party/reward activity capture; SystemQuit/process exit still does.
- A ship channel whose ship name contains a control (Cc) or format (Cf) character, or is longer than the name
  limit (128 scalars), is ignored (`GameLogParser`): the iPad Deck does not turn on and the theme does not change.
- QT: `Player Selected Quantum Target - Local` becomes the last observed local `destination`,
  independently of ship-channel state, entity binding or spawn (owner decision, 2026-10-29). A
  ship-channel join and ClearDriver keep it; leaving the current ship channel, vehicle destruction,
  PU exit, process/SystemQuit and log reset clear it. This confirms a selection, not a currently
  locked target or completed jump. Names remain technical (`displayName` null); `freeMode` and
  target-cleared events are not emitted because no verified source was established. Missing
  selection records cannot be reconstructed. QT alone does not enable the Deck or identify a ship.
- ATC `answered` means a hangar notification arrived after our request in the same context; there is no
  request id in the log, so a late answer to an earlier request can still be attributed to the newer one.
- Foreground check and `SendInput` are not atomic; focus can change in between (spec 41.4).
- If the process is killed, its watchdog cannot release keys (graceful exit always releases).
- If the anti-cheat denies reading the game's image path, focus falls back to the process name (logged).
- Chat `KEYEVENTF_UNICODE` typing, chat key toggling, hold durations and F8 still need in-game calibration.
- Self-destruct is protected only by the iPad slider; a paired client can send the allowed hold directly.

## Session rewards and contracts (protocol 41.21)

The existing reader now extracts explicit reward amounts and observed contract/objective notifications.
An authenticated iPad receives a bounded replay feed after welcome/full state, independently of coalesced ship
state. Up to 256 events are retained in memory and sent four at a time; acks/errors and state keep priority.
There is no extra reader/polling loop or PC ledger file. Totals, saved sessions and presentation belong to the iPad.

Purchases, sales, rentals, fines and wallet balance are excluded. Starting the server at EOF does not recover
earlier gameplay. Replay gaps are detectable; the feed is not a durable or complete financial record.
See section 41.21 in the FE specification and `SESSION-REWARDS-CONTRACTS.md` in the FE root for Mac validation.

## Session reliability follow-up (2026-10-28)

See [SESSION-RELIABILITY-FIXES.md](SESSION-RELIABILITY-FIXES.md) for the observed failures, source fixes,
verification results and the remaining Mac/game checks.

## Observed Party MVP (2026-10-28)

Party is read-only and always potentially incomplete. The PC emits only validated create/join, member
join/leave, leader and disband facts. All roster/history/confidence calculations run on the iPad. No extra
reader, polling or BE persistence. Party shares the 256-event replay with rewards/contracts.

No authoritative roster, local PU leave/kick, alias mapping or shard continuity is established.
Invites, marker stream-out and launch acceptances do not update membership. Update both sides together;
older activity clients may label new kinds as incomplete finance history. The running server is not replaced
automatically. See protocol section 41.24 in the iPad repository for the wire contract and test scenario.

Party validation on Windows: Release build passed with zero warnings; all 399 BE tests passed,
including mixed Party/reward WSS paging, reconnect replay and denial to unpaired clients. Formatting check
passed. A read-only replay of 37 real logs (2,621,104 lines) produced 43 Party facts, matching the independent
source scan: 1 creation, 6 local joins, 7 member joins, 16 member leaves, 10 leader changes and 3 disbands.
Swift tests are added but cannot run on this Windows machine. Before in-game testing, build/test the iPad
app on Mac and use the matching updated BE build. Check the Party tab in portrait/landscape and en/fr/ru.

## Local QT observation (owner decision, 2026-10-29)

The selected local QT destination is independent of ship identity. The existing single reader publishes
it even without a spawn, a recognized channel or a bound entity. No new reader, polling, timer, history or
input action is added. The wire shape stays unchanged. Spec 41.25 (`..\SCMFD\Claude outputs\CSMFD-discussion.md`)
is the source of truth. It drops the ship-ID binding requirement for QT (41.14) and the QT resets on a
ship-channel join and on ClearDriver (41.9); QT is still cleared when the ship goes away (`59bb202`).
Background and FE validation notes: `QT-LOCAL-TARGET-BE-HANDOFF.md` in the SCMFD repository root.
