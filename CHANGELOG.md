# Changelog

All notable changes to SignArtSaver will be documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.11.11] — 2026-05-19

Final CTO-pass before the public release. Four parallel reviewer agents
(security, correctness/lifecycle, docs/release-readiness, code-quality)
re-audited the 0.11.10 build and surfaced six items worth fixing.

### Fixed
- **Decompression-bomb guard in `TryResizePngBytes`** — apply / self-heal /
  admin-apply paths now refuse PNGs whose IHDR dimensions would decode
  beyond a 64 MiB RGBA budget (or are unreadable / out of the 8192-per-side
  sanity bound). Previously a 4 MiB flat-color PNG that decompressed to GBs
  of RGBA could OOM-kill the server through the public-gallery surface.
- **Self-heal ownership re-check ungated** — the entity-owner-vs-applier
  comparison at heal time now runs unconditionally, regardless of the
  `Require entity ownership for apply` config. Heal-time ownership is a
  safety property (don't silently repaint someone else's deployed sign
  post-raid), not a permission policy.
- **Share-modal contact list color tag** — the share modal's recipient
  names rendered default white because the rich-text tag was a malformed
  `<color=#1 0.95 0.6 1>` (stray `#` on rgba floats). Now `<color=#ffe699>`.
- **`UiShareModal` cleanup** — `DestroyAllUi` now tears down the share
  modal alongside the other CUI elements; previously a player who
  disconnected with the share modal open (or an Unload while it was open)
  left orphan CUI on the client until next reconnect.

### Documentation
- **README** — softened the URL safety-check description to honestly
  describe a deny-list (IP literals + private TLDs `.local`/`.internal`/etc.)
  rather than implying a host-allowlist that doesn't exist.
- **README + UMOD_DESCRIPTION** — trimmed the localization claim. ~41
  high-traffic keys (error responses, permission denials, common
  confirmations) are lang-routed today; usage hints, help-body text, and
  share/unshare overviews are English-only. Full coverage planned.

## [0.11.10] — 2026-05-19

First public release. The plugin has been in private development for some time;
this is the first version cleaned up and audited for general use. The 0.x version
line reflects the development history, not the maturity — the audit covered every
hook handler, every public surface, and the security/correctness/lifecycle/
release-readiness dimensions were each handled by a separate reviewer pass.

### Added
- Per-player image library for painted signs, photo frames, carvable pumpkins,
  paintable windows, neon signs, artist canvases, and reactive targets.
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
  server-side (decay / engine glitch / pixel-painter wipe). Drops the tracking
  record if the entity's owner has changed.
- Admin moderation: `/saveart admin <steamid> <list|apply|rename|remove|publish|unpublish> <slot>`.
- Oxide lang file with 41 keyed strings; ready for community translation.

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
| `Max PNG bytes per saved slot` | `4194304` | 4 MiB; Rust's engine caps ~2 MiB. |

### Permissions
- `signartsaver.use` — base. Auto-granted to the `default` group on first load (configurable via `Auto-grant signartsaver.use to default group on startup`).
- `signartsaver.admin` — bypass ownership; manage other players' libraries; access `/saveart debug`.

### Requirements
- Rust dedicated server (any current build with `PaintedItemStorageEntity`).
- Sign Artist plugin by Whispers88, v1.4.x (hard dependency — plugin self-unloads if absent).
- `libgdiplus` on Linux hosts. Install BEFORE starting RustDedicated; the System.Drawing path is initialized once at process startup.
- Carbon framework recommended; Oxide-compatible.

### Notes
- The `[JsonProperty]` description strings ARE the config keys — editing one between versions resets that field to the new default on upgrade. v1.0 onwards we'll treat these as stable. (If you're upgrading from a 0.x build, double-check `HidePvxOnSignAim` after first reload.)
- Sign Artist's `OnImagePost` fires synchronously before its download coroutine finishes. The byte capture is deferred via a 5-second retry-poll on the entity's CRC; if the URL is slow or Sign Artist times out, the slot is saved URL-only and bytes can be back-filled later via `/saveart save` while aimed at the sign.
- Cross-library applies (Public Gallery, Shared-with-me) refuse URL fallback — prevents server-side SSRF via artist-controlled URLs when a bytes file is missing.
