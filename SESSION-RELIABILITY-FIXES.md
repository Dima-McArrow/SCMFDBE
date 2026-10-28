# Game-session reliability investigation — 2026-10-28

> **Theme and Deck parts superseded by [FIX-07](FIX-07-one-path-deck-and-theme.md) (spec 41.23, 2026-10-28):** the channel-activity theme path described here was removed; ClearDriver no longer turns the Deck off. The command-deadline and input fixes still hold.

## Evidence and limits

Reviewed the PC diagnostic log in `%LocalAppData%/CSMFD/logs` and the latest LIVE/Game.log
(2026-10-27 22:50 UTC through 2026-10-28 04:35 UTC), alongside both repositories.
Times below are UTC; add one hour for Paris on these dates. No game input was sent during investigation.

- At 23:14 the PC recognized an RSI Aurora Mk I LN channel join and sent the updated state.
  Navigation/target lines named RSI_Aurora_GS_LN. The old display/model comparison failed, leaving
  its entity unknown, rejecting its QT target and ignoring its 23:26 ClearDriver.
- Anvil C8R Pisces Rescue similarly differs from ANVL_C8R_Pisces. Two explicit aliases now
  fix these verified pairs. Other variants still require evidence; this is not a universal ship database.
- At 00:43 the PC recognized an Aegis Redeemer join and sent state. The logs alone do not establish
  which iPad build/settings were active or why that particular theme did not change.
- The newer PC channel events already included opaque channelId values. The iPad ignored that
  field and had no consumer for channel leaves. The theme now follows channel activity independently
  of ClearDriver, with the existing state-based join as a fallback for older servers.
- The user confirmed that failed emotes open and close an empty chat, with a QWERTY keyboard.
  The existing text path used KEYEVENTF_UNICODE, which Windows delivers as VK_PACKET rather than
  ordinary key scan codes. This is a plausible cause, not proof about Star Citizen's internal input handling.
  See [Microsoft KEYBDINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput)
  and [VkKeyScanExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-vkkeyscanexw).
- At 03:06:14, after a PC server restart, authentication was rejected (4003); a new pairing succeeded
  at 03:06:35. The current trust file contains only the new device. Existing logs cannot establish
  how the old trust record ceased to be available. Restart alone does not reproduce the loss in tests.
  Other recorded disconnects mostly use normal closure (1000), which does not prove a network failure.

## Source changes

PC: verified ship aliases; apostrophes in ship-channel names; layout-mapped physical emote keystrokes;
chat-open wait increased to 300 ms and pre-submit wait to 100 ms. Text is validated before insertion;
unmappable characters and Ctrl/Alt mappings fail without submitting Enter. Partial insertion gets
release-only cleanup and never a text replay. Failed cleanup uses the existing input block/local reset.
Physical modifier/key conflicts are checked again before text insertion. The ordinary action tap minimum
remains 60 ms. Connection logs now include the close reason and distinguish a rejected stored token.

iPad: direct touch callbacks deliver press/release even if no pressed frame renders; cancellation
releases ordinary keys and never activates a timed hold. Channel themes use opaque identity, drop
duplicate/replayed events, wait for replay completion, ignore old-channel leaves, and return to SCMFD
on a matching leave or game-session end. Seat exit keeps the manufacturer theme. Manual theme selection
and Auto theme off remain available. Older saved channel events without channelId remain readable.

Failed socket sends now end the connection immediately. A pending command without an acknowledgement
for 15 seconds closes a still-responsive connection, settles pending commands as unknown, and allows
the existing reconnect policy to recover. Replaced connections settle outstanding tickets first.
No input is retried or replayed. Rejected authentication still requires explicit pairing.

## Verification

Backend: 345 tests pass, including fake-input timing/failure checks, pure Win32 input-plan checks,
Aurora/Pisces entity/QT/ClearDriver integration, and real trust-file round trips across reconstructed
pairing services. Tests never call real SendInput. The Release build has zero warnings/errors and formatting verification passes.
The tested Windows build is prepared in the BE publish folder; it was not started.

Frontend: Swift Testing cases added for quick touches, command deadlines, channel identity, replay,
history compatibility and themes. Xcode is unavailable on this Windows host: these changes are
not build-verified or test-verified on the iPad simulator yet.

No new dependencies, log readers, background polling or periodic network messages were added.
The emote mapping work occurs only when an emote is requested. No FPS claim is established by unit tests.

## Live verification still needed

1. Build/test the updated SCMFD scheme on a Mac simulator, then install the updated iPad app.
2. Run the updated PC build; keep the existing config, certificate and trust data.
3. Start with chat closed and the English QWERTY layout active. Try /wave, then an emote with an
   underscore. Observe both visible command text and the character animation; a sent ack only proves insertion.
4. With Auto theme enabled, join Aurora/Pisces, select a QT target, exit the seat, and later leave the
   matching ship channel. Expect QT updates, Deck off on ClearDriver, theme retained on seat exit,
   and SCMFD theme on the actual matching channel leave.
5. Try a brief tap and a held action, then background/resume the app and restart the PC server.
   Authentication should survive. If it does not, retain the new close-reason log and iPad Debug entries
   before re-pairing; do not publish tokens or trusted-devices.json.

The original trust-loss cause and live emote acceptance remain unconfirmed.
