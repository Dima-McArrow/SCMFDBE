# CLAUDE.md — CSMFD Windows server

Guidance for coding agents working in this repository. `AGENTS.md` points here; keep this file the single source.

## What this is

Windows tray app (C#, .NET 10) that is the PC side of CSMFD, a Star Citizen MFD on the iPad. It accepts a small
allowlist of commands from **one paired iPad** over WSS, injects them into the focused game with `SendInput`, tails
`Game.log` and publishes a compact state. The iPad app is a separate repository (`SCMFD`, SwiftUI), normally checked
out next to this one as `..\SCMFD`, with its own `CLAUDE.md`.

The contract is **protocol v2**, section 41 of `..\SCMFD\Claude outputs\CSMFD-discussion.md`; section 40 (v1) is
historical and not implemented. When code and spec disagree, the spec wins unless the user decides otherwise.
`README.md` covers features, data files and status; `docs/SERVER-IMPLEMENTATION.md` (Russian) is the design report.

## Build and test

```powershell
dotnet build Csmfd.slnx -c Release                 # analyzers + code style are errors
dotnet test  Csmfd.slnx -c Release                 # 266 tests, ~1 s; no real input is ever sent
dotnet format Csmfd.slnx --verify-no-changes       # must stay clean
dotnet publish src/Csmfd.Server -c Release -o publish   # publish/ is git-ignored
```

- SDK pinned by `global.json` (10.0.403, `latestFeature`). Windows only: `Csmfd.Server` and the tests target
  `net10.0-windows`.
- `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild` (`Directory.Build.props`).
  A warning is a broken build; fix it, do not add `NoWarn` or `#pragma` without asking.
- Package versions live only in `Directory.Packages.props`; `PackageReference` has no `Version`. Every project has a
  `packages.lock.json`: after a package change run `dotnet restore` and commit the lock files.
  `-p:ContinuousIntegrationBuild=true` restores in locked mode (what a clean clone must pass).
- Dev runs: set `CSMFD_DATA_DIR=<folder>` so config, certificate, trust and logs never touch `%LocalAppData%\CSMFD`.
  The single-instance mutex (`Local\CSMFD.Server`) ignores it: quit the tray copy before starting another build.
- `tools/Csmfd.Probe` (`csmfd-probe`) plays the iPad from the command line (pinned TLS, pair, one command, idle ping);
  usage is in `README.md`.

## Hard rules

- Scope is spec 41.2. No web UI, REST, Swagger, cloud, accounts, analytics, screen capture/OCR, memory reading, DLL
  injection, drivers, global keyboard hooks or polling, macros or arbitrary key names, auto-update, admin rights.
- No new NuGet dependencies without asking. Today: `QRCoder` and `Microsoft.Extensions.*`; tests add xUnit and
  `Microsoft.Extensions.TimeProvider.Testing`.
- Tests use **xUnit** (`[Fact]`, `[Theory]`; `Xunit` is a global using). Tests never call real `SendInput`.
- Code comments and all strings in English (tray text is not localized).
- Keep changes to what was asked; mention other problems instead of fixing them silently.
- Commit directly on `main`; no feature branches.

## Projects and dependency direction

`Csmfd.Protocol` ← `Csmfd.Core` ← `Csmfd.Server`. Never add references the other way.

| Project | TFM | Role |
|---|---|---|
| `src/Csmfd.Protocol` | `net10.0` | Wire contract v2: strict `ClientMessageParser`, hand-written `ServerMessageWriter`, `ActionCatalog` (allowlist), `KeyNames`, `ProtocolLimits`, `StateModel`. No dependencies |
| `src/Csmfd.Core` | `net10.0` | All logic, platform-neutral: `InputExecutor`, `CommandGate`, `ConnectionSession`, `ControllerRegistry`, `PairingService`, `StateStore`, `LogTailer` / `GameLogParser` / `GameLogProcessor`, `InputMonitor`. Talks to Windows only through `IInputSink`, `IGameWindow`, `IPhysicalKeyboard` |
| `src/Csmfd.Server` | `net10.0-windows` | Composition (`ServerApp`), Kestrel/WSS (`WssHost`), config and storage, certificate and trust store, Win32 (`SendInput`, focus, WinEvent hook, session lock, mDNS), WinForms tray |
| `tests/Csmfd.Tests` | `net10.0-windows` | `Protocol/`, `Core/` (on fakes), `Server/` (Win32-pure checks and loopback WSS end-to-end), `Fixtures/` |
| `tools/Csmfd.Probe` | `net10.0` | Dev client |

New behavior goes into `Core` with a test on fakes (`tests/Csmfd.Tests/Core/Fakes.cs`); `Server` stays a thin adapter.
Protocol and input decisions never live in the tray or in `WssHost`.

## Code conventions

- File-scoped namespaces. Naming (warnings = errors): `_camelCase` private fields, `s_camelCase` private static
  fields, PascalCase for constants and `static readonly`.
- Time comes from an injected `TimeProvider`. No `DateTime.Now/UtcNow`, `Stopwatch`, `Task.Delay`, `Thread.Sleep` in
  `Core` or `Protocol`; tests drive timers with `FakeTimeProvider`.
- Logging through source-generated `[LoggerMessage]` partial methods, as in the existing classes.
- Levels: Information for lifecycle, Warning for problems, Debug for decisions (why a Game.log line changed or did
  not change state, state sends). The file shows Debug only while the tray switch is on (`DebugLogging`, 15 min,
  then off by itself; off at every start). The factory lets Debug through and `FileLoggerProvider.MinimumLevel`
  decides, so a Debug call costs one comparison when the switch is off.
- Debug messages obey the same privacy rules as the rest: event types, offsets, revisions, entity ids, command ids
  and reasons are fine; the player handle (channel owner), raw Game.log lines, chat text, tokens, codes and full
  paths are not. `DebugLogExplainsDecisionsWithoutThePlayerHandle` pins the player-handle rule.
- P/Invoke: `LibraryImport` in `Win32/NativeMethods.cs`, blittable structs (`INPUT` size is tested).
- Files: `ServerStorage.WriteAtomically` (temp + move); JSON is camelCase, read case-insensitively.
- Wire timestamps: ISO 8601 UTC **with milliseconds**. UUIDs lowercase on the wire.
- Line endings LF (`.gitattributes`). `tests/**/Fixtures/**/*.log` are binary on purpose (CRLF, split UTF-8): never
  re-save them through an editor or a normalizing tool.

## Concurrency

- `InputExecutor` is the only owner of synthetic pressed keys and runs one gesture at a time. Everything that presses
  or releases goes through it; safety paths call `CancelAll(reason)`.
- The executor calls `IExecutorOutput` while holding its lock: implementations only enqueue, never block and never
  call back into the executor. `GameLogProcessor` never calls the executor while holding its own lock.
- Timer callbacks carry a token so a stale callback cannot release a newer gesture. Keep that pattern for new timers.
- `ConnectionSession`: one receive loop, one send loop. The receive loop never waits for input, the log or the
  network; JSON handling never calls `SendInput` directly. Ack/error go before state; state is coalesced.
- `ServerApp.StartAsync` runs on the UI thread (the WinEvent foreground hook and `SystemEvents` need its
  message loop).

## Safety release (spec 41.11 item 7)

Every trigger ends in the executor; none of them sends Esc/Enter or any "recovery" input.

| Trigger | Source | Executor call | Cancel reason |
|---|---|---|---|
| Game window loses focus | `GameMonitor` (WinEvent foreground hook) | `CancelAll` | `focus_lost` |
| Game process exits or the log shows the session end | `GameMonitor` (2 s / 10 s presence check), `GameLogProcessor` (SystemQuit) | `CancelAll` | `game_ended` |
| Session locked (Win+L) or detached (user switch, RDP disconnect, logoff) | `Win32/SessionMonitor` (`SystemEvents.SessionSwitch`) | `SetSessionLocked(true/false)` | `session_locked` |
| Leaving the seat (ClearDriver of our ship), ship channel join / leave, vehicle destroyed, log reset | `GameLogProcessor` | `CancelAll` | `ship_context_changed` |
| Tray "Pause input" | `TrayContext` | `SetPaused` | `paused` |
| Disconnect, heartbeat timeout or slow client, client replaced, revoke | `ConnectionSession` / `ControllerRegistry` | `CancelConnection` | `disconnected` / `watchdog` / `client_replaced` / `revoked` |
| Tap without `up` for 10 s | executor timer | — | `auto_released` (`watchdog`) |
| Listener stop, Quit | `ServerApp` | `CancelAll` | `disconnected` / `shutdown` |

- While the session is locked or detached, new gestures are rejected as `game_not_focused` (no new wire reason).
  Do not rely on the foreground window for this: with the secure desktop up it can still report the game.
- Lock and detach are two flags in `SessionMonitor`; input reopens only when both are clear (after a fast user
  switch, `ConsoleConnect` arrives while the lock screen is still shown).
- `SystemEvents` keeps static handlers: anything that subscribes must unsubscribe in `Dispose`.
- A new trigger goes through an executor method with a Core test on fakes; only the OS subscription lives in
  `Server/Win32`.

## Protocol

- The parser is strict (duplicate keys, depth 8, 16 KiB, UUIDs, allowlist). A `KeyCommand` carries the resolved
  `ActionSpec`, never raw strings, so nothing outside `ActionCatalog` can be pressed.
- The writer is a manual `Utf8JsonWriter` because a delta distinguishes a missing key from `"ship": null`. Its
  invariant checks throw: a throw there is a server bug, not something to catch.
- **Ship context has one path: `shipContext` in `state`** (owner decision 2026-10-28, spec 41.23). Transitions:
  `channelJoined`, `channelLeft` (only the current ship's channel), `vehicleDestroyed` (current or just-left ship,
  verified ejection line), `gameEnded`, `logReset`. ClearDriver is never a transition, it only releases input.
  Ship channels never go into `activity`. The iPad maps these to Deck on/off, theme and key reset (`FIX-07`);
  the server sends facts, never UI policy. A new reason is a wire change: fixture + iPad decoder first.
- `tests/Csmfd.Tests/Fixtures/Protocol/*.json` are the reference messages for both sides. A wire change updates
  together: spec section 41, the fixture, the writer/parser test, and (in the iPad repo) `SCMFD/Network/ProtocolV2.swift`.

## Changes that span both repositories

| Change | Here | In `..\SCMFD` |
|---|---|---|
| Wire format | `Csmfd.Protocol` + fixture + test | `ProtocolV2.swift`, `ServerSession.swift` + Swift Testing suite |
| New or changed action | `ActionCatalog` + its row in `ActionCatalogTests` | `ShipAction` (default binding must match key and mods exactly) |
| Emotes | `ServerApp.DefaultEmotes` (written to `emotes.txt` only when the file is missing) + count in `DefaultEmotesTests` | `Emote.swift`, then `python3 tools/gen-emotes-md.py <out.md>` produces the list |
| Pairing payload / port | `PairingForm`, `ProtocolLimits.Port` | `PairingPayload.swift` |

Do not edit the iPad repository from a task in this one unless the user asks; describe what the other side needs.

## Security rules that must not regress

- Listen only on the user-chosen private IPv4 of an active interface. Never `0.0.0.0`/`::`, never auto-pick a
  VPN-like adapter. Address gone → listener stops and input is released.
- `wss://` only, TLS 1.2+, single endpoint `/csmfd`, WebSocket compression off, requests with `Origin` or a query
  string rejected, at most 3 upgraded connections.
- Pairing opens only from the tray, lasts 120 s, 6-digit CSPRNG code, 5 failures in total per window. Tokens are
  stored only as SHA-256 and compared in constant time. The certificate is created once and kept under DPAPI
  (CurrentUser); never regenerate it silently.
- An unpaired client gets no state, paths, player name or Game.log data.
- Never log tokens, pairing codes, key contents, Game.log content or full local paths (`Microsoft.*` stays at Warning).
- Never retry or replay input. Release before closing a connection. Every new gesture re-checks game focus, the
  process and physical modifiers right before `SendInput`; the chat Enter is never sent outside the game.
- The input monitor is local, in memory and recording only while its window is open; it never goes to disk or the network.
- The process runs as an ordinary user. The firewall rule is a command the user copies and runs once as admin.

## Docs

`README.md` (English) is for users and developers; update its Status and Known limits when behavior changes.
`docs/SERVER-IMPLEMENTATION.md` is a dated report: change it only when the user asks.
