# SignArtSaver — uMod plugin-page description

*(Paste into the uMod plugin listing. README.md has the long-form install / config docs; this is the marketing description.)*

---

**SignArtSaver** is a per-player image library for Rust signs, photo frames, banners, carvable pumpkins, paintable windows, neon signs, artist canvases, and reactive targets. Built on top of [Sign Artist](https://umod.org/plugins/sign-artist) (Whispers88, required dependency) to give every artist on your server a persistent gallery of their work that survives wipes.

**Two capture paths, one library.** Paint with `/sil <url>` as usual and SignArtSaver auto-snapshots both the URL and the rendered PNG bytes. Or aim at any painted sign — vanilla pixel-painter, CopyPaste import, Sign Artist alike — and click **Save Sign** to capture the bytes directly. Apply prefers bytes (no re-download, no Discord-CDN expiry), auto-resizes to the target canvas, and falls back to URL when needed.

**Public gallery + commission workflow.** A built-in CUI panel lets players browse their library, preview at native canvas size, publish slots to a server-wide public gallery (with per-player cap), or share individual slots with specific buyers for commission/sell-art workflows. Admins get moderation surfaces — unpublish, rename, or remove slots in other players' libraries with `/saveart admin <steamid> ...`.

**Self-heal for blank signs.** A connect-time pass (and optional periodic timer) re-applies cached bytes if a tracked sign ever blanks server-side — decay, raid damage, vanilla painter wipes, or the occasional engine glitch. Ownership is re-checked at heal time, so a base that's changed hands won't get someone else's art painted back onto it.

**Carbon and Oxide compatible.** Hard dep on Sign Artist; no other plugin coupling. The most-visible player-facing strings — error responses, permission denials, common confirmations — live in `oxide/lang/<locale>/SignArtSaver.json` and are ready for community translation (41 keys today; fuller coverage planned). Two permissions: `signartsaver.use` (auto-granted to the `default` group, configurable) and `signartsaver.admin` (cross-library management).

---

## Features

- Persistent per-player image library — survives wipes.
- Auto-capture URL + bytes from Sign Artist's `OnImagePost`.
- Byte-mode manual save: works on any painted sign, regardless of how it was painted.
- Apply auto-resizes to the target canvas size via System.Drawing.
- Public Gallery: server-wide browseable, with per-player cap (default 25).
- Per-slot share / unshare for commission/sell workflows.
- In-game CUI browse panel: My Library / Public Gallery / Shared With Me tabs, search, preview modal.
- Self-heal pass on player connect and an optional periodic timer.
- Admin moderation: unpublish, rename, remove on any player's library.
- Lang-file ready (English defaults, drop-in translations).
- Atomic JSON saves; debounced flush-on-change.
- Five-pass security/correctness/release/code-quality audit before first public release.

## Coverage

Signage, PhotoFrame, CarvablePumpkin, PaintedItemStorageEntity (drawable windows, paintable reactive targets), artist canvases, neon signs, hanging banners, photo frames (all DLC variants).

## Requirements

- Rust dedicated server (current build with `PaintedItemStorageEntity`).
- Sign Artist plugin by Whispers88, v1.4.x. *Hard dep — SignArtSaver self-unloads if absent.*
- `libgdiplus` on Linux hosts. Install BEFORE first server start.
- Carbon framework recommended; Oxide-compatible.

## Permissions

- `signartsaver.use` — auto-granted to the `default` group on first boot. Disable in config if you gate features by group.
- `signartsaver.admin` — cross-library management + `/saveart debug`.

## Links

- Source: https://github.com/xphox2/SignArtSaver
- Issues / translations: GitHub issues.
- License: MIT.
