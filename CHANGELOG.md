# Changelog

All notable changes to SignArtSaver will be documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.11.14] — 2026-05-19

First public release. The plugin has been in private development for some time;
this is the first version cleaned up and audited for general use. The 0.x
version line reflects the development history, not the maturity — the audit
covered every hook handler and public surface, with security, correctness,
lifecycle, release-readiness, and code-quality each handled by a separate
reviewer pass, plus a separate compliance pass against uMod's plugin-submission
standards.

### Added
- Per-player image library for painted signs, photo frames, hanging signs, sign
  posts, banners, neon signs, artist canvases, carvable pumpkins, and all DLC
  art frames (light-up, gold, scrap).
- Auto-capture URL + bytes from Sign Artist's `OnImagePost` (deferred poll so
  bytes are captured AFTER Sign Artist's download coroutine finishes; not
  before — see notes).
- Byte-mode manual save via `/saveart save` for any painted sign you own
  (vanilla painter, Sign Artist, CopyPaste import — works on all of them).
- Apply path prefers byte-mode (no network round-trip, no Discord-CDN expiry
  worry) with auto-resize to the target canvas via `System.Drawing`.
- Public gallery: artists can `/saveart publish <slot>` and any player can
  browse + apply public art via the in-game panel. Per-player cap configurable
  (default 25; 0 = unlimited).
- Share-to-specific-buyer workflow: `/saveart share <slot> <name>` for
  commission / sell-art arrangements. Buyer sees the slot in their
  "Shared with me" tab.
- Self-heal: on player connect (and optional periodic timer), the plugin
  re-applies cached bytes to any tracked sign whose CRC has been zeroed
  server-side (decay / engine glitch / pixel-painter wipe). Heal-time
  ownership re-check is unconditional — drops the tracking record if the
  entity's owner has changed since the original apply.
- Admin moderation: `/saveart admin <steamid> <list|apply|rename|remove|publish|unpublish> <slot>`.
- Oxide lang file with 115 keyed strings — every player-facing message
  (usage hints, errors, confirmations, help body, share/unshare overviews,
  `/saveart debug` raycast output) is translatable.
- Decompression-bomb guard on apply: PNGs whose IHDR dimensions would
  decode beyond a 64 MiB RGBA budget (or are outside an 8192-per-side sanity
  bound) are refused before allocation.
- Dynamic `OnPlayerInput` hook subscription — only active while at least
  one pending save / apply / import is armed.

### Configuration defaults
| Key | Default | Notes |
|---|---|---|
| `Slots per player` | `50` | Per-player library cap. |
| `Auto-capture URLs from OnImagePost` | `true` | Snapshot every `/sil` paint. |
| `Require entity ownership for save` | `true` | Sane PvP-server default. |
| `Require entity ownership for apply` | `true` | Admin bypasses. |
| `Strict entity-kind match on apply` | `true` | Sign-to-sign, frame-to-frame, etc. |
| `Self-heal: enabled` | `true` | Survives raid / decay blanking. |
| `Max public slots per player` | `25` | Public Gallery contribution cap. |
| `Max PNG bytes per saved slot` | `2097152` | 2 MiB; matches Rust's FileStorage upload cap. Lower freely for disk hygiene; raising above 2 MiB triggers a startup warning since slots above the engine cap save but never apply. |

The full default config is also shipped as
[`SignArtSaver.example.json`](SignArtSaver.example.json) — copy it to your
config dir for a fresh install, or diff it against your running config to see
what you've customized.

### Permissions
- `signartsaver.use` — base. Auto-granted to the `default` group on first load (configurable via `Auto-grant signartsaver.use to default group on startup`).
- `signartsaver.admin` — bypass ownership; manage other players' libraries; access `/saveart debug`.

### Requirements
- Rust dedicated server (current build).
- Sign Artist plugin by Whispers88, v1.4.x (hard dependency — `// Requires: SignArtist` directive defers load until the dep is available).
- `libgdiplus` on Linux hosts. Install BEFORE starting RustDedicated; the `System.Drawing` path is initialized once at process startup.
- Carbon framework recommended; Oxide-compatible.

### uMod compliance
- No `System.Reflection` usage anywhere in the plugin.
- Every player-facing string routes through the Lang API (115 keys; English
  defaults auto-generated, drop-in translations supported).
- `OnPlayerInput` is subscribed dynamically — off by default, only active
  while a player has a pending save / apply / import.
- Permission system gates every entry point (`signartsaver.use` /
  `signartsaver.admin`); permissions auto-register on first load.
- Operator-friendly clamps + warnings in `ValidateConfig` for every
  configurable knob.
- Explicit `// Reference: Facepunch.System` and `// Reference: Rust.Data`
  directives so uMod's submission compiler can resolve `ListHashSet<>` and
  `NetworkableId` (Carbon auto-links both; Oxide doesn't).

### Notes
- The `[JsonProperty]` description strings ARE the config keys — editing one between versions resets that field to the new default on upgrade. v1.0 onwards we'll treat these as stable.
- Sign Artist's `OnImagePost` fires synchronously before its download coroutine finishes. The byte capture is deferred via a 5-second retry-poll on the entity's CRC; if the URL is slow or Sign Artist times out, the slot is saved URL-only and bytes can be back-filled later via `/saveart save` while aimed at the sign.
- Cross-library applies (Public Gallery, Shared-with-me) refuse URL fallback — prevents server-side SSRF via artist-controlled URLs when a bytes file is missing. IP-literal hosts and private TLDs (`.local`, `.internal`, `.localhost`, `.lan`, `.home`) are also denied on this path.
