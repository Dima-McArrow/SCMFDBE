# CSMFD Server (Windows BE)

Minimal, secured Windows companion for the CSMFD iPad MFD (Star Citizen). It accepts a small allowlist of
commands from **one paired iPad**, injects them into the focused game with `SendInput`, reads new
`Game.log` lines and publishes a compact state. Contract: **protocol v2**, spec section 41 of
`CSMFD-discussion.md`. Section 40 (v1) is historical and not implemented.

What it deliberately does **not** do: no web UI, no REST, no cloud/accounts/analytics, no screen capture/OCR,
no memory reading, no DLL injection or driver, no arbitrary macros or key names, no auto-update, no admin rights.

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

Status · Pair iPad · Network · Trusted devices (revoke one / all) · Pause input (releases immediately) ·
Re-enable input (only after a failed release) · Copy firewall command · Open data folder · Quit.

## Data (`%LocalAppData%\CSMFD`, current user only)

| File | Content |
|---|---|
| `config.json` | `bindAddress`, `gameExePath`, `gameLogPath`, `chatKey` (`enter`/`f12`), `displayName`, `serverId` |
| `server-certificate.bin` | ECDSA P-256 certificate + key, PKCS#12 protected with DPAPI (CurrentUser). Created once; deleting it requires re-pairing |
| `trusted-devices.json` | Device id, name, pairing time and **SHA-256 of the token** (never the token) |
| `emotes.txt` | Chat allowlist, one base name per line (`/name` and `/name<N>` accepted). **Sync with `Emote.swift`** |
| `logs\csmfd*.log` | Lifecycle and warnings only, 3 × 1 MiB. No tokens, codes, keys or Game.log content |

## How it maps to the spec

| Area | Implementation |
|---|---|
| Wire contract (41.6–41.10) | `Csmfd.Protocol`: strict parser (duplicates, depth 8, 16 KiB, UUIDs, allowlist), writer enforcing state invariants, JSON fixtures in `tests/Csmfd.Tests/Fixtures/Protocol` |
| Transport (41.6, 41.7) | `ConnectionSession`: one receive + one send loop, 1003/1007/1009, hello 5 s, heartbeat 15 s, >100 msg/s → 4005, bounded queues, 2 s send deadline, coalesced state (20 ms, ClearDriver urgent) |
| Pairing & trust (41.5) | `PairingService`, `ServerCertificateStore`, `FileTrustStore`; replacement releases old input before the new client acts; revoke closes 4006 |
| Input (41.11, 41.12) | `InputExecutor`: one gesture at a time (`busy`), 60 ms tap minimum without blocking, hold deadlines with stale-timer guard, 10 s ceiling, focus/process + physical modifier checks right before input, safety release on focus loss, lock, game exit, ClearDriver, disconnect, pause, shutdown; chat sequence never presses Enter outside the game |
| Win32 | `SendInput` scan codes / MOUSEINPUT / KEYEVENTF_UNICODE, image-path focus check, WinEvent foreground hook, no keyboard polling |
| Game.log (41.13, 41.14) | `LogTailer` (EOF start, byte offsets, generations), `GameLogParser` (verified lines of build 12913470), `GameLogProcessor` (ship context, QT target, ATC with watermark) |

## Known limits (honest list)

- Ship control is **assumed** from the ship channel join; ClearDriver ends it. No confirmed seat state exists.
- QT: only `Player Selected Quantum Target - Local` for the bound ship becomes `destination` (internal name,
  `displayName` null). `freeMode` is never emitted: no verified line yet.
- ATC `answered` means a hangar notification arrived after our request in the same context; there is no
  request id in the log, so a late answer to an earlier request can still be attributed to the newer one.
- Foreground check and `SendInput` are not atomic; focus can change in between (spec 41.4).
- If the process is killed, its watchdog cannot release keys (graceful exit always releases).
- If the anti-cheat denies reading the game's image path, focus falls back to the process name (logged).
- Chat `KEYEVENTF_UNICODE` typing, chat key toggling, hold durations and F8 still need in-game calibration.
- Self-destruct is protected only by the iPad slider; a paired client can send the allowed hold directly.
- Performance budgets (41.15) are targets until measured on the gaming PC.
