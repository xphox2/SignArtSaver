# SignArtSaver

A persistent per-player image library for Rust signs, photo frames, banners, carvable pumpkins, paintable windows, neon signs, artist canvases, and reactive targets. Built on top of [Sign Artist](https://umod.org/plugins/sign-artist) (Whispers88) — required dep.

Designed for servers where painted-sign art is a feature, not a one-wipe novelty: every artist on the server gets their own gallery that survives wipes, a public-gallery surface for sharing creations, and a commission/sell workflow with per-slot buyer access. Art is captured at apply time as both URL (for human reference) and raw PNG bytes (for reliable replay even after Discord-CDN expiry or `/sil` URL rot).

---

## Quick start

1. Install [Sign Artist](https://umod.org/plugins/sign-artist) (required dep).
2. On Linux hosts, install `libgdiplus` BEFORE starting RustDedicated:
   ```bash
   sudo apt install libgdiplus
   ```
   `System.Drawing` uses libgdiplus for apply-time PNG resizing; without it signs render white. The library is loaded at RustDedicated startup, not at plugin load — install before first launch.
3. Drop `SignArtSaver.cs` into `oxide/plugins/` (Carbon: `carbon/plugins/`).
4. The plugin auto-grants `signartsaver.use` to the `default` group on first boot (configurable via `Auto-grant signartsaver.use to default group on startup`).
5. Paint a sign with `/sil <url>` — the slot is auto-captured. Open the panel with `/saveart`.

That's it. Players can paint, save, browse, share, and re-apply across wipes.

---

## Requirements

| Component | Minimum | Notes |
|---|---|---|
| Rust dedicated server | Current build with `PaintedItemStorageEntity` | Older Rust (pre-2024 draftish) lacks the type — plugin will fail to load with a clear error. |
| Sign Artist | v1.4.x | Hard dep. Plugin self-unloads if absent. |
| `libgdiplus` (Linux) | Any recent | For System.Drawing PNG resizing. |
| Carbon framework | Any current | Oxide-compatible; both supported. |

---

## Permissions

| Permission | Auto-granted? | What it does |
|---|---|---|
| `signartsaver.use` | Yes, to `default` group (configurable) | All player commands and the `/saveart` browse panel. |
| `signartsaver.admin` | No — grant manually | Bypass ownership checks; cross-library management; `/saveart debug`. |

Operators can disable the auto-grant in config and gate access through their own permission groups (members-only servers, etc.).

---

## Commands

### Player

| Command | Description |
|---|---|
| `/saveart` | Open the browse panel (My Library tab). |
| `/saveart save [name]` | Save the painted sign you're aiming at (byte-mode; works on any painted sign, regardless of how it was painted). |
| `/saveart apply <slot>` | Arm the apply — look at YOUR sign and press USE within the timeout. |
| `/saveart rename <slot> <new name>` | Rename a slot (1–32 printable chars, no `<` `>` newlines). |
| `/saveart remove <slot>` | Two-step delete (run twice within the confirm window). |
| `/saveart wipe` | Two-step wipe of the entire library. |
| `/saveart publish <slot>` | Toggle a slot public/private. Adds to the Public Gallery. |
| `/saveart public [search]` | Open the Public Gallery tab (optional name/artist filter). |
| `/saveart share <slot> <name\|steamid>` | Grant a specific buyer copy access (commission/sell workflow). |
| `/saveart unshare <slot> <name\|steamid>` | Revoke a buyer's access. |
| `/saveart shared [slot]` | List buyers on a slot, or all your shared slots. |
| `/saveart shared-with-me` | List slots other artists have shared with you. |
| `/saveart list [page]` | Plain-chat list of your own slots. |
| `/saveart help` | Help text. |

### Admin (`signartsaver.admin`)

| Command | Description |
|---|---|
| `/saveart admin <steamid> list` | List another player's slots. |
| `/saveart admin <steamid> apply <slot>` | Apply another player's slot to YOUR aimed sign. |
| `/saveart admin <steamid> rename <slot> <name>` | Rename a slot in another player's library (moderation). |
| `/saveart admin <steamid> remove <slot>` | Delete a slot from another player's library (moderation). |
| `/saveart admin <steamid> publish <slot>` | Publish a slot on another player's behalf (rare). |
| `/saveart admin <steamid> unpublish <slot>` | Take a slot down from the Public Gallery (moderation). |
| `/saveart debug` | Raycast diagnostic — prints what your crosshair is hitting. |

Admins bypass the per-player public-gallery cap.

---

## Configuration

Generated at `oxide/config/SignArtSaver.json` (Carbon: `carbon/configs/SignArtSaver.json`) on first load. Most defaults work for a typical server; adjust as needed.

### Library

| Key | Default | Notes |
|---|---|---|
| `Slots per player (1-500)` | `50` | Per-player library cap. |
| `Max public slots per player...` | `25` | Cap on Public Gallery contribution. `0` = unlimited. Admins bypass. |
| `Max PNG bytes per saved slot...` | `4194304` (4 MiB) | Defense-in-depth disk-fill guard. Rust's engine already caps PNG uploads near 2 MiB. |
| `Strip query string when hashing for dedupe` | `true` | Treats `?token=...` variants of the same URL as duplicates. |
| `Block URLs containing these substrings...` | `["token=","auth=","Authorization","?key="]` | Substring blocklist for auto-capture. The cross-library URL-fallback path is also denied for IP literals and private TLDs (`.local`, `.internal`, `.localhost`, `.lan`, `.home`). |
| `Warn when saving Discord CDN URLs (they expire ~24h)` | `true` | Saved anyway; the warning suggests imgur / GitHub raw / self-hosted for wipe-survival. |

### Capture / apply behavior

| Key | Default | Notes |
|---|---|---|
| `Auto-capture URLs from OnImagePost` | `true` | Whole UX assumes this is on. |
| `Require entity ownership for save` | `true` | Recommended for any server with adversarial dynamics. |
| `Require entity ownership for apply` | `true` | Admin bypasses. |
| `Strict entity-kind match on apply` | `true` | Sign-to-sign, frame-to-frame, etc. Prevents stretched applies. |
| `Apply cooldown per player (seconds)` | `2.0` | Anti-spam. |
| `Pending-apply timeout (seconds)` | `30.0` | Armed slot expires if no USE key. |

### UI

| Key | Default | Notes |
|---|---|---|
| `CUI rows per page (4-16)` | `8` | Browse panel page size. |
| `Truncate URL display length (16-120)` | `40` | Long URLs trimmed with `…`. |
| `Confirm-delete window (seconds, 1-30)` | `5.0` | Two-step delete confirm. |
| `Wipe-confirm window (seconds, 5-60)` | `10.0` | Two-step wipe confirm. |

### Raycast

| Key | Default | Notes |
|---|---|---|
| `Aim distance for /saveart save and /saveart apply raycasts (meters; 1-30)` | `8.0` | How far the chat / UI raycast reaches when looking for a sign in your aim. |

### Self-heal

| Key | Default | Notes |
|---|---|---|
| `Self-heal: re-apply cached bytes...` | `true` | Survives the "sign blanks server-side" bug. |
| `Self-heal: max repairs per applied-entity in 24h` | `3` | Repair-cap before flagging an entity. |
| `Self-heal: clone bytes into the applier's own library...` | `true` | Lets buyer art survive even if the original artist deletes their slot. |
| `Self-heal diagnostic: verbose per-entity logging` | `false` | One log line per scanned slot. Off in steady-state. |
| `Self-heal diagnostic: periodic scan interval in minutes` | `0` | `0` = off. `1-60` runs the verify pass for every online player on a recurring timer. Diagnostic — enable when bracketing a trigger. |

### Permissions

| Key | Default | Notes |
|---|---|---|
| `Auto-grant signartsaver.use to default group on startup` | `true` | Fresh installs "just work". Operators who gate features by group can flip this `false` and grant manually. |

> **⚠ Note on upgrades:** Oxide deserializes config by the EXACT description string. If a future release changes a description, the value at the old key is silently reset to the new default. Re-check non-default settings after every plugin update.

---

## Public Gallery

`/saveart publish <slot>` adds a byte-mode slot to a server-wide gallery. Other players can browse via `/saveart public [search]`, preview, and apply public art to their own signs. Byte-mode only — URL-only slots can't be published (the original-owner's `signartist.url` permission isn't transferable).

### Moderation

- Per-player cap (config: `Max public slots per player`, default 25) limits how many slots any one player can contribute to the gallery. Admins bypass.
- Admins can take down a slot with `/saveart admin <steamid> unpublish <slot>` or rename it with `... rename <slot> <new name>`.
- Slot names are sanitized at the save source — angle brackets, control chars, and rich-text tags are stripped before the name reaches anyone else's chat.

### Sharing

Different from publishing: `/saveart share <slot> <name|steamid>` gives a SPECIFIC buyer copy access. Buyer sees the slot in their "Shared with me" tab, can preview, can apply. Built for commission/sell workflows.

- Per-(artist, recipient) cooldown (5 min) prevents share-spam loops.
- Cross-library URL fallback is refused — the buyer's server will never fetch the artist's URL on their behalf (no SSRF).

---

## Self-heal

Painted signs occasionally lose their image server-side (decay near limit, engine glitch, vanilla painter wipe, sometimes a Sign Artist `Store` failure). Self-heal scans every tracked sign on player connect (and optionally on a periodic timer) and re-applies the cached bytes if the entity's CRC has been zeroed.

The 24-hour repair cap stops runaway loops if the trigger keeps firing. Ownership is re-checked at heal time — if a base has changed hands since the original applier touched the sign, the tracking record is dropped instead of repainting someone else's canvas.

For diagnosing a recurring trigger, set `Self-heal diagnostic: periodic scan interval in minutes` to a small value (e.g. `1`) and watch the server log. The trigger window is logged on every detected break.

---

## Localization

The most-visible player-facing strings — common error responses, permission denials, ownership refusals, the highest-traffic confirmations — live in `oxide/lang/<locale>/SignArtSaver.json` (Carbon: `carbon/lang/<locale>/`). English defaults are auto-generated on first load; community translations can be dropped in by adding a new `<locale>/SignArtSaver.json` file with the same keys. 41 keys today; fuller coverage (usage hints, help-body text, share/unshare overviews, diagnostic raycast output) is planned for a future release.

Translators: `string.Format` placeholders (`{0}`, `{1}`, etc.) for parameterized templates. If a translation has a placeholder/arg-count mismatch, the helper falls back to the English default with a `PrintWarning` so the operator can spot the bad translation.

Server-operator log messages (`Puts`, `PrintWarning`, `PrintError`) stay English by design — the server log is operator-only.

---

## Known limitations

- **Sign Artist version pin** — audited against 1.4.x. Versions outside that range print a startup warning; auto-capture and replay may silently miss if the hook signature or API_Skin* arguments change.
- **Older Rust builds** — the plugin references `PaintedItemStorageEntity` directly. Servers running a Rust build that pre-dates that type will fail to load; an alternative dynamic-type fallback is planned but not in this release.
- **Discord CDN URLs** — Discord rotates CDN URLs ~24 hours. A slot's URL goes stale, but the cached bytes survive wipes and are preferred on apply. Players save imgur / GitHub raw / their own host for URL durability.
- **Auto-capture timing** — Sign Artist's `OnImagePost` fires synchronously; the actual download finishes a few seconds later. SignArtSaver defers the byte capture via a 5-second retry-poll. On slow CDNs the slot is saved URL-only and bytes can be back-filled by re-running `/saveart save` while aimed at the painted sign.

---

## Console commands

The plugin registers ~25 `signartsaver.ui.*` console commands. These are internal — they're invoked by the CUI buttons in the browse panel. Not a public API; don't script against them.

---

## Credits

- Hard dep on [Sign Artist](https://umod.org/plugins/sign-artist) by Whispers88 — the URL-paint workhorse.
- Author: Xphox.
- License: MIT — see [`LICENSE`](LICENSE).

---

## Issues, contributions, translations

GitHub: https://github.com/xphox2/SignArtSaver

Please file bugs with: Rust build number, Carbon/Oxide version, Sign Artist version, plugin version, and the server log lines tagged `[SignArtSaver]` from around the issue.
