// Reference: System.Drawing
//
// SignArtSaver — per-player image library for painted signs, photo frames,
// carvable pumpkins, paintable windows, neon signs, artist canvases, and
// reactive targets. Two capture paths feed one library:
//
//   1) URL auto-capture — subscribes to Sign Artist's OnImagePost hook;
//      whenever an artist runs /sil <url>, both the URL and the rendered
//      PNG bytes are captured to the slot (bytes via deferred poll because
//      Sign Artist's download is async).
//   2) Byte-mode manual save — /saveart save reads the image bytes directly
//      off the painted sign you're aiming at via FileStorage. Works on any
//      painted sign — vanilla painter, Sign Artist, drawable windows, etc.
//      No URL needed; survives Discord-CDN expiry forever.
//
// Bytes are stored as PNG files under <data-dir>/SignArtSaver/images/
// <steamid>/<slot>.png; the JSON keeps metadata pointers. Apply prefers
// byte-mode (no network round-trip), with own-library URL fallback when
// the bytes file is missing. Cross-library applies (public gallery,
// shared-with-me) refuse URL fallback by design — prevents SSRF.
//
// REQUIREMENTS:
//   - Rust dedicated server (any current build with PaintedItemStorageEntity;
//     pre-2024 builds will fail to load — older Rust lacked the type).
//   - Sign Artist plugin by Whispers88, v1.4.x (hard dep — plugin self-unloads
//     if absent). Available on uMod.
//   - libgdiplus on Linux hosts (apt: `libgdiplus`). System.Drawing uses it
//     for the apply-time PNG resize step. Without it, signs render white.
//     Loaded at RustDedicated startup — install BEFORE first launch.
//   - Carbon framework recommended; Oxide-compatible.
//
// HOOKS SUBSCRIBED:
//   OnImagePost          — Sign Artist's post-paint notification (auto-capture)
//   OnPlayerInput        — USE-key dispatch for armed apply requests
//   OnPlayerConnected    — self-heal sweep + display-name roster stamp
//   OnPlayerSleepEnded   — display-name roster stamp
//   OnPlayerDisconnected — drop transient state (CUI, cooldowns)
//   OnServerSave         — flush data file
//
// SIGN ARTIST API CONSUMED (matched at runtime against the loaded version):
//   API_SkinSign(BasePlayer, Signage, string url, bool raw=false, uint textureIndex=0)
//   API_SkinPhotoFrame(BasePlayer, PhotoFrame, string url, bool raw=false)
//   API_SkinPumpkin(BasePlayer, CarvablePumpkin, string url, bool raw=false)
//
// PERMISSIONS:
//   signartsaver.use     — base; auto-granted to default group on first load (configurable).
//   signartsaver.admin   — bypass ownership, manage other players' libraries, /saveart debug.
//
// DATA + CONFIG:
//   Data:    <data-dir>/SignArtSaver/players.json  (survives wipes; atomic writes)
//   Images:  <data-dir>/SignArtSaver/images/<steamid>/<slot>.png
//   Config:  <config-dir>/SignArtSaver.json
//   Lang:    <lang-dir>/<locale>/SignArtSaver.json  (auto-generated; translatable)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
// Disambiguate types that exist in both UnityEngine and System.Drawing.
using SDImage = System.Drawing.Image;
using SDBitmap = System.Drawing.Bitmap;
using SDGraphics = System.Drawing.Graphics;
using SDRectangle = System.Drawing.Rectangle;

namespace Oxide.Plugins
{
    [Info("SignArtSaver", "Xphox", "0.11.11")]
    [Description("Per-player image library with public gallery: byte-mode save + auto-resize on apply + Sign Artist URL capture + self-heal on connect/timer. Covers Signage, PhotoFrame, CarvablePumpkin, PaintedItemStorageEntity. Survives wipes.")]
    public class SignArtSaver : RustPlugin
    {
        [PluginReference] private Plugin SignArtist;

        #region Constants

        private const string PermUse   = "signartsaver.use";
        private const string PermAdmin = "signartsaver.admin";
        private const string DataFileName = "SignArtSaver/players";

        // Persisted in JSON; do not rename casually.
        private const string KindSign       = "Sign";
        private const string KindPhotoFrame = "PhotoFrame";
        private const string KindPumpkin    = "Pumpkin";
        // PaintedItemStorageEntity (v0.11.1) — drawable windows, paintable reactive targets,
        // anything else that holds painted bytes via a private _currentImageCrc field instead
        // of Signage's textureIDs[]/PhotoFrame's _overlayTextureCrc. Sibling class to all
        // three above (extends BaseEntity directly).
        private const string KindPaintedItem = "PaintedItem";

        // Reflection cache for PaintedItemStorageEntity._currentImageCrc — the field is
        // private with no public setter; the only vanilla write path is the Server_UpdateImage
        // RPC (which gates by item-in-inventory). For self-heal/apply we set the field
        // directly. If a future Rust patch renames the field, _paintedItemCrcField goes null
        // and the apply path fails gracefully with a clear error rather than NRE'ing.
        private static readonly FieldInfo _paintedItemCrcField =
            typeof(PaintedItemStorageEntity).GetField(
                "_currentImageCrc",
                BindingFlags.Instance | BindingFlags.NonPublic);

        // CUI element ids — strict SignArtSaver.* namespace to avoid collisions.
        private const string UiPanel       = "SignArtSaver.Ui.Panel";
        private const string UiHeader      = "SignArtSaver.Ui.Header";
        private const string UiTitle       = "SignArtSaver.Ui.Title";
        private const string UiCloseBtn    = "SignArtSaver.Ui.CloseBtn";
        private const string UiTabs        = "SignArtSaver.Ui.Tabs";
        private const string UiSearchBg    = "SignArtSaver.Ui.SearchBg";
        private const string UiSearchInput = "SignArtSaver.Ui.SearchInput";
        private const string UiPreview     = "SignArtSaver.Ui.Preview";
        private const string UiPreviewImg  = "SignArtSaver.Ui.PreviewImg";
        private const string UiHelpModal   = "SignArtSaver.Ui.Help";
        private const string UiBackdrop    = "SignArtSaver.Ui.Backdrop";
        private const string UiImportModal = "SignArtSaver.Ui.Import";
        private const string UiShareModal  = "SignArtSaver.Ui.Share";

        // Placeholder rendered in the Import modal's URL fields so the click zone is
        // visible. DispatchImport strips this if the user pressed Enter without typing —
        // CUI input fields don't auto-clear placeholders on focus.
        private const string ImportPlaceholder = "Click here, paste URL, press Enter";
        private const string UiColHeader   = "SignArtSaver.Ui.ColHeader";
        private const string UiRows        = "SignArtSaver.Ui.Rows";
        private const string UiPagination  = "SignArtSaver.Ui.Pagination";
        private const string UiStatus      = "SignArtSaver.Ui.Status";
        private const string UiEmptyHint   = "SignArtSaver.Ui.EmptyHint";

        // Tab indices (persisted in transient BrowsePanel.Tab; not in JSON).
        private const int TabMine   = 0;
        private const int TabPublic = 1;
        private const int TabShared = 2; // v0.10.1 — slots other artists have shared with me

        // Canvas → friendly name + image dimensions. Cribbed from Sign Artist 1.4.6 lines
        // 890+ (ImageSizePerAsset). Used for: (a) display ("Tall Picture Frame"), (b) the
        // resize target when applying bytes to a differently-sized canvas, (c) optional
        // strict-size match.
        private static readonly Dictionary<string, (string Name, int W, int H)> CanvasInfo
            = new Dictionary<string, (string, int, int)>(StringComparer.OrdinalIgnoreCase)
        {
            // Picture frames
            ["sign.pictureframe.landscape"] = ("Landscape Picture Frame", 256, 192),
            ["sign.pictureframe.portrait"]  = ("Portrait Picture Frame",  205, 256),
            ["sign.pictureframe.tall"]      = ("Tall Picture Frame",      128, 512),
            ["sign.pictureframe.xl"]        = ("XL Picture Frame",        512, 512),
            ["sign.pictureframe.xxl"]       = ("XXL Picture Frame",       1024, 512),
            // Wooden signs
            ["sign.small.wood"]             = ("Small Wooden Sign",       256, 128),
            ["sign.medium.wood"]            = ("Wooden Sign",             512, 256),
            ["sign.large.wood"]             = ("Large Wooden Sign",       512, 256),
            ["sign.huge.wood"]              = ("Huge Wooden Sign",        1024, 256),
            // Banners
            ["sign.hanging.banner.large"]   = ("Large Hanging Banner",    256, 1024),
            ["sign.pole.banner.large"]      = ("Large Pole Banner",       256, 1024),
            // Hanging signs
            ["sign.hanging"]                = ("Two Sided Hanging Sign",  256, 512),
            ["sign.hanging.ornate"]         = ("Two Sided Ornate Hanging Sign", 512, 256),
            // Sign posts
            ["sign.post.single"]            = ("Single Sign Post",        256, 128),
            ["sign.post.double"]            = ("Double Sign Post",        512, 512),
            ["sign.post.town"]              = ("Town Sign Post",          512, 256),
            ["sign.post.town.roof"]         = ("Town Sign Post (Roof)",   512, 256),
            // Photo frames
            ["photoframe.large"]            = ("Large Photo Frame",       320, 240),
            ["photoframe.portrait"]         = ("Portrait Photo Frame",    320, 384),
            ["photoframe.landscape"]        = ("Landscape Photo Frame",   320, 240),
            // Pumpkins (note: Sign Artist 1.4.6 lists this as 256×256; we keep 128×128 to
            // match prior behavior — if pumpkin art looks wrong, switch to 256×256.)
            ["carvable.pumpkin"]            = ("Carvable Pumpkin",        128, 128),
            // Artist canvas (sign.artistcanvas.*) — added v0.11.1
            ["sign.artistcanvas.xs"]        = ("XS Artist Canvas",        192, 256),
            ["sign.artistcanvas.m"]         = ("M Artist Canvas",         320, 240),
            ["sign.artistcanvas.l"]         = ("L Artist Canvas",         256, 640),
            ["sign.artistcanvas.xl"]        = ("XL Artist Canvas",        512, 512),
            ["sign.artistcanvas.xxl"]       = ("XXL Artist Canvas",       1024, 512),
            // Neon signs — added v0.11.1
            ["sign.neon.xl.animated"]       = ("XL Neon Sign (Animated)", 256, 256),
            ["sign.neon.xl"]                = ("XL Neon Sign",            256, 256),
            ["sign.neon.125x215.animated"]  = ("Neon Sign (Animated)",    128, 256),
            ["sign.neon.125x215"]           = ("Neon Sign",               128, 256),
            ["sign.neon.125x125"]           = ("Small Neon Sign",         128, 128),
            // DLC art frames — these are PhotoFrame subclasses, EntityKind=PhotoFrame
            ["lightupframe.small"]          = ("Light-up Frame Small",    128, 175),
            ["lightupframe.standing"]       = ("Light-up Frame Standing", 128, 175),
            ["lightupframe.large"]          = ("Light-up Frame Large",    320, 256),
            ["lightupframe.xl"]             = ("Light-up Frame XL",       512, 512),
            ["lightupframe.xxl"]            = ("Light-up Frame XXL",      1024, 512),
            ["goldframe.small"]             = ("Gold Frame Small",        256, 1024),
            ["goldframe.standing"]          = ("Gold Frame Standing",     128, 320),
            ["goldframe.large"]             = ("Gold Frame Large",        320, 256),
            ["goldframe.xl"]                = ("Gold Frame XL",           512, 512),
            ["goldframe.xxl"]               = ("Gold Frame XXL",          1024, 512),
            ["scrapframe.small"]            = ("Scrap Frame Small",       128, 175),
            ["scrapframe.standing"]         = ("Scrap Frame Standing",    128, 320),
            ["scrapframe.large"]            = ("Scrap Frame Large",       256, 1024),
            ["scrapframe.xl"]               = ("Scrap Frame XL",          512, 512),
            ["scrapframe.xxl"]              = ("Scrap Frame XXL",         1024, 512),
            // Other paintable entities — added v0.11.1
            ["spinner.wheel.deployed"]      = ("Spinning Wheel",          512, 512),
            ["paintable_reactive_target.deployed"] = ("Paintable Reactive Target", 256, 256),
            ["window.paintable"]            = ("Paintable Window",        512, 256),
        };

        #endregion

        #region Config

        private Configuration config;

        private class Configuration
        {
            [JsonProperty("Auto-capture URLs from OnImagePost")]
            public bool AutoCapture = true;

            [JsonProperty("Slots per player (1-500)")]
            public int SlotsPerPlayer = 50;

            [JsonProperty("Require entity ownership for save")]
            public bool RequireOwnerSave = true;

            [JsonProperty("Require entity ownership for apply (admin perm bypasses)")]
            public bool RequireOwnerApply = true;

            [JsonProperty("Strict entity-kind match on apply (refuse PhotoFrame->Sign etc.)")]
            public bool StrictKindMatch = true;

            [JsonProperty("CUI rows per page (4-16)")]
            public int RowsPerPage = 8;

            [JsonProperty("Truncate URL display length (16-120)")]
            public int UrlDisplayLen = 40;

            [JsonProperty("Confirm-delete window (seconds, 1-30)")]
            public double ConfirmDeleteSeconds = 5.0;

            [JsonProperty("Wipe-confirm window (seconds, 5-60)")]
            public double WipeConfirmSeconds = 10.0;

            [JsonProperty("Apply cooldown per player (seconds, 0-30)")]
            public double ApplyCooldownSeconds = 2.0;

            [JsonProperty("Strip query string when hashing for dedupe")]
            public bool StripQueryForDedupe = true;

            // ObjectCreationHandling.Replace: without this, Newtonsoft APPENDS the
            // deserialized values to the field's existing default list rather than
            // replacing it. The bug compounds — every plugin reload doubled the count of
            // the same default entries. Caught while inspecting the live config during
            // the v0.11.10 ZonePVxInfo rip-out; before the fix the JARS server had
            // accumulated 65× "token=", "auth=", "Authorization", "?key=" entries.
            [JsonProperty("Block URLs containing these substrings (case-insensitive)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> UrlBlocklist = new List<string> { "token=", "auth=", "Authorization", "?key=" };

            [JsonProperty("Warn when saving Discord CDN URLs (they expire ~24h)")]
            public bool WarnOnDiscordCdn = true;

            [JsonProperty("Auto-grant signartsaver.use to default group on startup")]
            public bool AutoGrantDefaultGroup = true;

            [JsonProperty("Max public slots per player (cap on Public Gallery contribution; 0 = unlimited; 1-500)")]
            public int MaxPublicSlotsPerPlayer = 25;

            [JsonProperty("Pending-apply timeout (seconds; armed slot expires if no USE-key)")]
            public double PendingApplyTimeoutSeconds = 30.0;

            [JsonProperty("Aim distance for /saveart save and /saveart apply raycasts (meters; 1-30)")]
            public float AimRangeMeters = 8f;

            [JsonProperty("Max PNG bytes per saved slot (defense-in-depth disk-fill guard; Rust's engine already caps PNG uploads near 2MB)")]
            public int MaxBytesPerSlot = 4 * 1024 * 1024;

            // ---- Self-heal (v0.11.0) ----
            // Background: photo-frame and sign textureIDs can end up zeroed server-side without an
            // observable trigger (suspected: Sign Artist's Remove-before-Store path on Store failure,
            // engine UpdateSign with empty bytes, or vanilla pixel-painter wipe). We can't always
            // identify the trigger but we can defend against it. On connect we walk every slot the
            // connecting player has applied (regardless of who owns the slot — applies are tracked
            // by AppliedByUserId) and re-write the cached PNG bytes if the entity's CRC is 0 or its
            // FileStorage row is missing.
            [JsonProperty("Self-heal: re-apply cached bytes when an applied sign loses its CRC or FileStorage row")]
            public bool SelfHealEnabled = true;

            [JsonProperty("Self-heal: max repairs per applied-entity in a 24h window before it's flagged and skipped")]
            public int SelfHealMaxRepairsPer24h = 3;

            [JsonProperty("Self-heal: clone bytes into the applier's own library when applying someone else's shared slot (so artist deletions don't orphan buyer art)")]
            public bool SelfHealCloneSharedBytes = true;

            // Diagnostic flags (v0.11.2). Off by default — enable while investigating a
            // recurring blank-art bug. Verbose logging emits one log line per scanned slot
            // (healthy or broken) plus failure-mode details on each repair. Periodic scan
            // tightens the trigger window: instead of only scanning on connect, fires every
            // N minutes for every online player so we can bracket WHEN the trigger fired.
            [JsonProperty("Self-heal diagnostic: verbose per-entity logging (off=quiet; on=one log line per scanned slot with state details)")]
            public bool SelfHealVerboseLogging = false;

            [JsonProperty("Self-heal diagnostic: periodic scan interval in minutes (0=off; 1-60=enabled). Helps bracket WHEN a trigger fires by running the verify pass for online players on a recurring timer instead of only on connect.")]
            public int SelfHealPeriodicScanMinutes = 0;
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception("config is null");
            }
            catch (Exception e)
            {
                PrintError($"Config load failed ({e.Message}); regenerating default.");
                LoadDefaultConfig();
            }
            ValidateConfig();
            SaveConfig();
        }

        private void ValidateConfig()
        {
            if (config.SlotsPerPlayer < 1 || config.SlotsPerPlayer > 500)
            {
                PrintWarning($"Invalid SlotsPerPlayer {config.SlotsPerPlayer}; clamping.");
                config.SlotsPerPlayer = Mathf.Clamp(config.SlotsPerPlayer, 1, 500);
            }
            if (config.RowsPerPage < 4 || config.RowsPerPage > 16)
            {
                PrintWarning($"Invalid RowsPerPage {config.RowsPerPage}; clamping.");
                config.RowsPerPage = Mathf.Clamp(config.RowsPerPage, 4, 16);
            }
            if (config.UrlDisplayLen < 16 || config.UrlDisplayLen > 120)
            {
                PrintWarning($"Invalid UrlDisplayLen {config.UrlDisplayLen}; clamping.");
                config.UrlDisplayLen = Mathf.Clamp(config.UrlDisplayLen, 16, 120);
            }
            if (config.ConfirmDeleteSeconds < 1 || config.ConfirmDeleteSeconds > 30)
                config.ConfirmDeleteSeconds = Mathf.Clamp((float)config.ConfirmDeleteSeconds, 1f, 30f);
            if (config.WipeConfirmSeconds < 5 || config.WipeConfirmSeconds > 60)
                config.WipeConfirmSeconds = Mathf.Clamp((float)config.WipeConfirmSeconds, 5f, 60f);
            if (config.ApplyCooldownSeconds < 0 || config.ApplyCooldownSeconds > 30)
                config.ApplyCooldownSeconds = Mathf.Clamp((float)config.ApplyCooldownSeconds, 0f, 30f);
            if (config.PendingApplyTimeoutSeconds < 5 || config.PendingApplyTimeoutSeconds > 300)
                config.PendingApplyTimeoutSeconds = Mathf.Clamp((float)config.PendingApplyTimeoutSeconds, 5f, 300f);
            if (config.AimRangeMeters < 1 || config.AimRangeMeters > 30)
                config.AimRangeMeters = Mathf.Clamp(config.AimRangeMeters, 1f, 30f);
            if (config.UrlBlocklist == null) config.UrlBlocklist = new List<string>();
            else
            {
                // Legacy clean-up (v0.11.10): pre-fix configs accumulated duplicates of the
                // default entries on every reload (Newtonsoft List<T> default behavior was
                // append-not-replace). Dedup once; next SaveConfig writes the clean list.
                int before = config.UrlBlocklist.Count;
                config.UrlBlocklist = config.UrlBlocklist.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                int removed = before - config.UrlBlocklist.Count;
                if (removed > 0) PrintWarning($"UrlBlocklist had {removed} duplicate entries; deduped on load.");
            }
            if (config.SelfHealMaxRepairsPer24h < 0 || config.SelfHealMaxRepairsPer24h > 50)
                config.SelfHealMaxRepairsPer24h = Mathf.Clamp(config.SelfHealMaxRepairsPer24h, 0, 50);
            if (config.SelfHealPeriodicScanMinutes < 0 || config.SelfHealPeriodicScanMinutes > 60)
                config.SelfHealPeriodicScanMinutes = Mathf.Clamp(config.SelfHealPeriodicScanMinutes, 0, 60);
            if (config.MaxPublicSlotsPerPlayer < 0 || config.MaxPublicSlotsPerPlayer > 500)
            {
                PrintWarning($"Invalid MaxPublicSlotsPerPlayer {config.MaxPublicSlotsPerPlayer}; clamping.");
                config.MaxPublicSlotsPerPlayer = Mathf.Clamp(config.MaxPublicSlotsPerPlayer, 0, 500);
            }
            // 64KiB floor — anything smaller refuses every reasonable PNG. 32MiB ceiling —
            // beyond that the engine wouldn't accept it anyway and the disk-fill protection
            // becomes meaningless. Default 4MiB sits well above Rust's ~2MiB engine cap.
            const int MinBytesPerSlot = 64 * 1024;
            const int MaxBytesPerSlotCap = 32 * 1024 * 1024;
            if (config.MaxBytesPerSlot < MinBytesPerSlot || config.MaxBytesPerSlot > MaxBytesPerSlotCap)
            {
                PrintWarning($"Invalid MaxBytesPerSlot {config.MaxBytesPerSlot}; clamping.");
                config.MaxBytesPerSlot = Mathf.Clamp(config.MaxBytesPerSlot, MinBytesPerSlot, MaxBytesPerSlotCap);
            }
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        #endregion

        #region Localization (v0.11.6)

        // uMod expects player-facing strings to live in oxide/lang/<locale>/SignArtSaver.json
        // (Carbon: carbon/lang/...) so server operators can install community translations
        // without editing the .cs. Keys cover the highest-visibility messages — chat error
        // responses, common confirmations. PrintWarning/PrintError/Puts targeting the server
        // operator stay in English (server log is operator-only). Future patches extend
        // coverage; the framework here is stable.
        //
        // Templates use string.Format placeholders ({0}, {1}, …). Slot names + display
        // names interpolated into templates are pre-sanitized by SanitizeSlotName and/or
        // EscapeRich at the call site so a translator can't accidentally enable rich-text
        // injection by reformatting the template.
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"]       = "You don't have permission to use SignArtSaver.",
                ["AdminOnly"]          = "Requires signartsaver.admin.",
                ["BadSteamId"]         = "Bad steamid.",
                ["NotOwner"]           = "You don't own that sign.",
                ["NoSignInAim"]        = "No sign in your aim. Re-open /saveart and try again.",
                ["NoImageOnSign"]      = "That sign has no image yet. Paint it first (vanilla painter or /sil <url>).",
                ["LookAtSign"]         = "Look at a sign, photo frame, or pumpkin within 8m.",
                ["SaveTimedOut"]       = "Save request timed out — open /saveart and click Save sign again.",
                ["ApplyTimedOut"]      = "Apply request timed out — re-arm with /saveart apply <slot>.",
                ["UrlBlocked"]         = "URL contains a blocked substring (auth token?); not saved.",
                ["UrlBlockedAtApply"] = "Slot {0} URL refused at apply time: {1}.",
                ["SignArtistOffline"]  = "Sign Artist plugin offline. Try again later.",
                ["CooldownActive"]     = "Slow down — try again in {0:0.0}s.",
                ["SlotNotFound"]       = "No such slot ({0}).",
                ["SlotArmed"]          = "Slot {0} armed. Look at the sign and press USE within {1:0}s.",
                ["SlotArmedNamed"]     = "Slot {0} (\"{1}\") armed. Look at YOUR sign and press USE within {2:0}s.",
                ["SlotApplied"]        = "Applied slot {0} (\"{1}\"{2}).",
                ["SlotAppliedUrl"]     = "Applied slot {0} (\"{1}\", URL).",
                ["SlotRenamed"]        = "Slot {0} renamed to \"{1}\".",
                ["SlotRemoved"]        = "Removed slot {0}.",
                ["SlotPublishedTpl"]   = "Slot {0} (\"{1}\") {2}{3}.",
                ["NamePolicy"]         = "Name must be 1-32 printable chars (no < > newlines).",
                ["BytesMissing"]       = "Slot {0} bytes missing — the artist must re-save with byte data. Cross-player URL replay is disabled.",
                ["NoUsableData"]       = "Slot {0} has no usable data — bytes file missing and no URL on record. Re-save needed.",
                ["UrlOnlyCantPublish"] = "Slot {0} is URL-only; can't publish without bytes. Run /saveart save while looking at the painted sign first.",
                ["UrlOnlyCantShare"]   = "Slot {0} is URL-only; can't share without bytes. Run /saveart save while looking at the painted sign first.",
                ["PublishCapHit"]      = "Public-gallery cap reached ({0}/{1}). Unpublish one with /saveart publish <slot> first.",
                ["LibraryFull"]        = "Library full ({0}/{1}). /saveart remove <slot> to free space.",
                ["LibraryFullAuto"]    = "Library full ({0}/{1}). Auto-capture is paused — /saveart remove <slot> to free space.",
                ["AlreadySavedDup"]   = "Already saved as slot {0} (\"{1}\").",
                ["ConfirmDelete"]      = "Confirm delete: re-run /saveart remove {0} within {1:0}s.",
                ["WipeConfirmPrompt"]  = "This will delete ALL your saved art. Run /saveart wipe confirm within {0:0}s to proceed.",
                ["WipeConfirmDone"]    = "Wiped {0} slots.",
                ["WipeConfirmExpired"] = "Wipe confirmation expired. Run /saveart wipe first, then /saveart wipe confirm within the window.",
                ["WipeEmpty"]          = "Library was already empty.",
                ["ShareSelfDenied"]    = "Can't share with yourself.",
                ["ShareAlready"]       = "Already shared with <color=#7ad>{0}</color> on {1:yyyy-MM-dd}.",
                ["ShareDone"]          = "<color=#55ff55>Shared</color> slot {0} (\"{1}\") with <color=#7ad>{2}</color>. Total buyers: {3}.",
                ["UnshareNotOnList"]   = "<color=#7ad>{0}</color> wasn't on slot {1}'s buyer list.",
                ["UnshareDone"]        = "<color=#ffcc55>Revoked</color> <color=#7ad>{0}</color> from slot {1}. Remaining buyers: {2}.",
                ["UnshareEmpty"]       = "Slot {0} has no buyers to revoke.",
            }, this, "en");
        }

        // Resolve a localized template via Oxide's lang system, substituting args via
        // string.Format. Pass player=null for server-context messages (defaults to "en").
        // Defensive against:
        //   - missing key (lang.GetMessage returns null) → returns the key itself.
        //   - translator typo `{N}` in a template with N args → catches FormatException
        //     AND ArgumentNullException; falls back to English default.
        //   - args.Length==0 — short-circuits without calling string.Format (avoids the
        //     surprise where a translator added a `{0}` to a key callers expect to be literal).
        private string L(string key, BasePlayer player, params object[] args)
        {
            var s = lang.GetMessage(key, this, player?.UserIDString);
            if (s == null) return key;
            if (args == null || args.Length == 0) return s;
            try { return string.Format(s, args); }
            catch (Exception e)
            {
                PrintWarning($"Lang format error for key '{key}': {e.Message}. Falling back to English.");
                var en = lang.GetMessage(key, this, "en") ?? key;
                try { return string.Format(en, args); } catch { return en; }
            }
        }

        #endregion

        #region Data

        private PlayerStore store;
        private bool _dataLoadFailed;

        private class PlayerStore
        {
            public Dictionary<ulong, PlayerLibrary> Players = new Dictionary<ulong, PlayerLibrary>();
            // Server-wide name roster for share-by-name lookups (v0.10.1). Stamped on every
            // connect / sleep-end so even players who haven't published / saved any art are
            // resolvable. Survives wipes (lives in players.json).
            public Dictionary<ulong, KnownPlayer> KnownPlayers = new Dictionary<ulong, KnownPlayer>();
        }

        // Roster entry — one per Steam ID we've seen. Name is the most recent display name;
        // LastSeenUtc lets the resolver prefer recently-seen matches when a partial name is
        // ambiguous between a current player and someone who hasn't logged in for months.
        private class KnownPlayer
        {
            public string Name;
            public DateTime LastSeenUtc;
        }

        private class PlayerLibrary
        {
            // Monotonic; gaps allowed after delete to keep slot ids stable for muscle memory.
            public int NextSlotId = 1;
            public List<SavedArt> Slots = new List<SavedArt>();
            public DateTime LastUpdatedUtc;
            // Cached display name of the library owner, refreshed on connect/wake. Used for
            // attribution in the public gallery; null/empty falls back to the steamid string.
            public string OwnerName;
        }

        private class SavedArt
        {
            public int Slot;
            public string Name;
            // URL-mode metadata (optional — null for byte-only captures via /saveart save).
            public string Url;
            public string UrlHash;     // sha1[0..10] of normalized URL (dedupe key)
            public bool Raw;
            // Byte-mode payload (optional — null for legacy URL-only slots, but every v0.2.0+
            // slot has it). BytesPath is relative to <data-dir>/SignArtSaver/, e.g.
            // "images/76561198XXXXXXXXX/3.png".
            public string BytesPath;
            public string BytesSha1;   // sha1[0..10] of raw PNG bytes (byte-mode dedupe key)
            public long BytesSize;     // file size in bytes (display only)
            // Common fields.
            public string EntityKind;  // KindSign | KindPhotoFrame | KindPumpkin | KindPaintedItem
            public uint TextureIndex;
            public DateTime SavedUtc;
            // Original-canvas tracking (v0.3.0). Lets the artist see "this was drawn on a
            // Tall Picture Frame" when browsing, and lets us pick the right resize target
            // when applying to a different canvas type.
            public string OriginalShortPrefab;  // e.g. "sign.pictureframe.xl"
            public string OriginalCanvasName;   // e.g. "XL Picture Frame"
            public int OriginalImageWidth;      // pixels (Sign Artist's ImageWidth, used for resize)
            public int OriginalImageHeight;
            // Public gallery (v0.3.0). When IsPublic, the slot shows up in the Public tab
            // for everyone with signartsaver.use; anyone can apply it (read-only — only the
            // owner can rename / unpublish / delete).
            public bool IsPublic;
            public DateTime? PublishedUtc;
            // Per-slot allowlist (v0.10.1). Each entry is a player the artist has explicitly
            // granted copy access — they can preview + apply this slot to their own signs.
            // Multi-buyer: same slot can have many entries (commission/sell flow). Public is
            // a strict superset, so an IsPublic slot ignores SharedWith for access purposes
            // (the artist can keep the list as a record for later unpublishing if they want).
            public List<ShareEntry> SharedWith = new List<ShareEntry>();

            // Tracking for self-heal (v0.11.0). Every successful byte-mode apply records the
            // target entity here so OnPlayerConnected can later verify the entity's CRC still
            // resolves in FileStorage, and re-write from cache if not. AppliedByUserId is the
            // *applier* — for shared/public art that may be a different player than the slot
            // owner. Self-heal scans by AppliedByUserId so a buyer's art still heals on the
            // buyer's connect even if the original artist never logs in again.
            public List<AppliedEntity> AppliedEntities = new List<AppliedEntity>();
        }

        private class ShareEntry
        {
            public ulong SteamId;
            // Display name snapshotted at share time so the artist's UI can show
            // who they shared with even if the buyer renames or never logs in again.
            public string NameAtShare;
            public DateTime SharedUtc;
        }

        private class AppliedEntity
        {
            public ulong NetId;             // BaseNetworkable.net.ID.Value of the painted entity
            public ulong AppliedByUserId;   // who ran the apply (slot owner if self-applied; buyer if shared)
            public string ApplyKind;        // KindSign | KindPhotoFrame | KindPumpkin | KindPaintedItem
            public uint TextureIndex;       // multi-texture signs use indices 0..3; photo frames/pumpkins always 0
            public DateTime AppliedUtc;
            public DateTime? LastVerifiedUtc;
            public DateTime? LastRepairedUtc;
            public int RepairCount;         // sliding 24h count; reset on first repair after a quiet period
            // Diagnostic (v0.11.2): the first scan tick at which we observed broken state
            // since the last successful repair. Cleared on repair. Combined with
            // LastVerifiedUtc this brackets the trigger window — "art was healthy at X,
            // broken at Y, so the trigger fired in (X, Y]". Higher periodic scan rate
            // narrows this window proportionally.
            public DateTime? LastObservedBrokenUtc;
        }

        private void LoadData()
        {
            try
            {
                store = Interface.Oxide.DataFileSystem.ReadObject<PlayerStore>(DataFileName);
                if (store == null) store = new PlayerStore();
                if (store.Players == null) store.Players = new Dictionary<ulong, PlayerLibrary>();
                if (store.KnownPlayers == null) store.KnownPlayers = new Dictionary<ulong, KnownPlayer>();
                // Forward-compat: pre-0.10.1 saves have null SharedWith on every slot;
                // pre-0.11.0 saves have null AppliedEntities. Initialize both lazily.
                // Backfill (v0.11.3): pre-0.11.3 names may contain rich-text/control chars;
                // those leaked into share notifications. Sanitize on load so legacy data is
                // safe even before the artist renames the slot.
                foreach (var lib in store.Players.Values)
                {
                    if (lib?.Slots == null) continue;
                    foreach (var s in lib.Slots)
                    {
                        if (s == null) continue;
                        if (s.SharedWith == null) s.SharedWith = new List<ShareEntry>();
                        if (s.AppliedEntities == null) s.AppliedEntities = new List<AppliedEntity>();
                        if (!string.IsNullOrEmpty(s.Name))
                        {
                            var cleaned = SanitizeSlotName(s.Name);
                            if (cleaned.Length > 32) cleaned = cleaned.Substring(0, 32);
                            if (cleaned != s.Name) s.Name = cleaned;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                PrintError($"Data load failed ({e.Message}); starting empty store. Existing file (if any) will be backed up before overwrite.");
                store = new PlayerStore();
                _dataLoadFailed = true;
            }
        }

        private void SaveData()
        {
            if (store == null) return;
            var finalPath = Path.Combine(Interface.Oxide.DataDirectory, DataFileName + ".json");
            if (_dataLoadFailed)
            {
                try
                {
                    if (File.Exists(finalPath))
                    {
                        var bak = finalPath + ".bak." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                        File.Copy(finalPath, bak, overwrite: false);
                        PrintWarning($"Backed up corrupt data file to {bak} before overwriting with empty store.");
                    }
                }
                catch (Exception e) { PrintWarning($"Backup attempt failed (non-fatal): {e.Message}"); }
                _dataLoadFailed = false;
            }

            // Atomic write (v0.11.4): serialize to a sibling .tmp, then rename into place.
            // File.Replace is a single inode swap on POSIX filesystems — no half-written
            // window where a crash could leave players.json empty or corrupt. Falls back
            // to DataFileSystem.WriteObject if the temp-write path itself fails (e.g. disk
            // full, permissions) so we never silently drop a save.
            try
            {
                var dir = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var tmpPath = finalPath + ".tmp";
                var json = JsonConvert.SerializeObject(store, Formatting.Indented);
                File.WriteAllText(tmpPath, json);
                if (File.Exists(finalPath))
                    File.Replace(tmpPath, finalPath, destinationBackupFileName: null);
                else
                    File.Move(tmpPath, finalPath);
                return;
            }
            catch (Exception e)
            {
                PrintWarning($"Atomic save failed ({e.Message}); falling back to DataFileSystem.WriteObject.");
            }
            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, store);
        }

        // Stamp lib.LastUpdatedUtc and arm the debounced save. Call this in place of any
        // direct write to lib.LastUpdatedUtc so mutations get flushed to disk within
        // SaveDebounceSeconds instead of waiting for the next OnServerSave.
        //
        // CAUTION (lessons learned the hard way in v0.11.4 → 0.11.8): the body assigns
        // to lib.LastUpdatedUtc directly — do NOT rewrite that line via a bulk-replace
        // regex that targets `<var>.LastUpdatedUtc = DateTime.UtcNow;`, or this helper
        // becomes infinitely recursive on every mutation.
        private void StampLib(PlayerLibrary lib)
        {
            if (lib == null) return;
            lib.LastUpdatedUtc = DateTime.UtcNow;
            MarkDirty();
        }

        // Schedule a debounced SaveData. Successive calls within SaveDebounceSeconds reset
        // the timer — a burst of share/rename/publish mutations only triggers one write,
        // but the data file is never more than SaveDebounceSeconds stale after the burst.
        private void MarkDirty()
        {
            if (_unloading || store == null) return;
            _saveDebounceTimer?.Destroy();
            _saveDebounceTimer = timer.Once(SaveDebounceSeconds, () =>
            {
                _saveDebounceTimer = null;
                try { SaveData(); }
                catch (Exception e) { PrintError($"Debounced SaveData threw: {e}"); }
            });
        }

        private PlayerLibrary GetOrCreate(ulong userId)
        {
            if (!store.Players.TryGetValue(userId, out var lib))
            {
                lib = new PlayerLibrary();
                store.Players[userId] = lib;
            }
            if (lib.Slots == null) lib.Slots = new List<SavedArt>();
            if (lib.NextSlotId < 1) lib.NextSlotId = 1;
            return lib;
        }

        // ---- Share access (v0.10.1) ----

        // True when `viewerId` is allowed to preview / apply this slot owned by `ownerId`.
        // Owner always wins; public is everyone; explicit allowlist; admins bypass.
        // viewer is the BasePlayer (used only for the admin check) — pass null to skip
        // the admin path (e.g. console-only contexts).
        private bool CanAccessSlot(SavedArt art, ulong ownerId, ulong viewerId, BasePlayer viewer)
        {
            if (art == null) return false;
            if (viewerId == ownerId) return true;
            if (art.IsPublic) return true;
            if (art.SharedWith != null)
            {
                for (int i = 0; i < art.SharedWith.Count; i++)
                {
                    if (art.SharedWith[i].SteamId == viewerId) return true;
                }
            }
            if (viewer != null && HasAdmin(viewer)) return true;
            return false;
        }

        // Resolve a chat-typed identifier into a steamid. Accepts:
        //   - 17-digit steamid (always wins; doesn't even need to be in the roster)
        //   - exact-match name (case-insensitive) against KnownPlayers + activePlayerList
        //   - partial-match (case-insensitive Contains) — only resolves if exactly ONE match
        // Returns false with `err` populated for ambiguity / not-found cases.
        // canonicalName is the resolved name (for display in confirmation messages).
        // `caller` is the chat sender — excluded from match candidates so artists don't
        // accidentally share with themselves on a partial-name typo.
        private bool TryResolvePlayer(string nameOrId, BasePlayer caller, out ulong steamId, out string canonicalName, out string err)
        {
            steamId = 0;
            canonicalName = null;
            err = null;
            if (string.IsNullOrWhiteSpace(nameOrId)) { err = "No name or steamid given."; return false; }
            string s = nameOrId.Trim();

            // Steamid64 fast path.
            if (ulong.TryParse(s, out var asId) && asId.IsSteamId())
            {
                steamId = asId;
                canonicalName = ResolveDisplayName(asId);
                return true;
            }

            ulong callerId = caller != null ? (ulong)caller.userID : 0UL;

            // Two candidate sets. Exact-match runs against BOTH; substring only against
            // online (security: a hostile artist can't passively-target offline players via
            // partial-match without typing a steamid — closes a chat-spam vector pre-fix).
            var allCandidates = new Dictionary<ulong, string>();      // online + offline roster
            var onlineCandidates = new Dictionary<ulong, string>();   // currently-connected only
            foreach (var kv in store.KnownPlayers)
            {
                if (kv.Key == callerId) continue;
                var n = kv.Value?.Name;
                if (string.IsNullOrEmpty(n)) continue;
                allCandidates[kv.Key] = n;
            }
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !p.userID.IsSteamId()) continue;
                ulong pid = (ulong)p.userID;
                if (pid == callerId) continue;
                if (!string.IsNullOrEmpty(p.displayName))
                {
                    allCandidates[pid] = p.displayName;
                    onlineCandidates[pid] = p.displayName;
                }
            }

            // Exact match (case-insensitive) wins outright — handles two players whose
            // names differ only by suffix ("Bob" vs "Bobby"), where exact "Bob" should
            // not be ambiguous.
            ulong exactId = 0; string exactName = null; int exactCount = 0;
            foreach (var kv in allCandidates)
            {
                if (string.Equals(kv.Value, s, StringComparison.OrdinalIgnoreCase))
                {
                    exactId = kv.Key; exactName = kv.Value; exactCount++;
                }
            }
            if (exactCount == 1) { steamId = exactId; canonicalName = exactName; return true; }
            if (exactCount > 1)
            {
                err = $"Multiple players match exact name '{s}'. Use a steamid.";
                return false;
            }

            // Substring match — online players only, must be unique to resolve.
            ulong subId = 0; string subName = null; int subCount = 0;
            var examples = new List<string>(8);
            foreach (var kv in onlineCandidates)
            {
                if (kv.Value.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    subId = kv.Key; subName = kv.Value; subCount++;
                    if (examples.Count < 5) examples.Add(kv.Value);
                }
            }
            if (subCount == 1) { steamId = subId; canonicalName = subName; return true; }
            if (subCount == 0) { err = $"No online player matched '{s}' (offline players need exact name or steamid)."; return false; }
            err = $"{subCount} online players match '{s}': {string.Join(", ", examples)}{(subCount > examples.Count ? ", …" : "")} — be more specific or use a steamid.";
            return false;
        }

        // Best-effort display name for a steamid. Online > roster > raw id string.
        private string ResolveDisplayName(ulong steamId)
        {
            if (!steamId.IsSteamId()) return steamId.ToString();
            var p = BasePlayer.FindByID(steamId);
            if (p != null && !string.IsNullOrEmpty(p.displayName)) return p.displayName;
            if (store.KnownPlayers.TryGetValue(steamId, out var k) && !string.IsNullOrEmpty(k.Name)) return k.Name;
            // Library-owner fallback — older saves stamped OwnerName before KnownPlayers existed.
            if (store.Players.TryGetValue(steamId, out var lib) && !string.IsNullOrEmpty(lib.OwnerName)) return lib.OwnerName;
            return steamId.ToString();
        }

        // Notify the buyer in-game when a slot is shared with them. If they're offline
        // we don't queue — the slot simply appears in their "Shared with me" tab on next
        // /saveart open. Per design decision (no offline-notification persistence in v1).
        // Anti-spam: per-(artist, recipient) cooldown stops unshare+reshare loops being
        // used to grief the recipient's chat. Artist + slot names are rich-text-escaped
        // since they land in another player's chat (sanitizing slot name at write doesn't
        // cover legacy data already on disk; escape here as belt-and-suspenders).
        private void NotifyShareAdded(ulong recipientId, ulong artistId, SavedArt art)
        {
            var p = BasePlayer.FindByID(recipientId);
            if (p == null || !p.IsConnected) return;
            var key = (artistId, recipientId);
            if (shareNotifyCooldown.TryGetValue(key, out var last) &&
                (DateTime.UtcNow - last).TotalSeconds < ShareNotifyCooldownSeconds)
                return;
            shareNotifyCooldown[key] = DateTime.UtcNow;
            string artistName = EscapeRich(ResolveDisplayName(artistId));
            string slotName = string.IsNullOrEmpty(art.Name) ? $"slot {art.Slot}" : $"\"{EscapeRich(art.Name)}\"";
            p.ChatMessage(Tag() + $"<color=#55ff55>{artistName}</color> shared <color=#ffff66>{slotName}</color> with you. Open <color=#7ad>/saveart</color> → <color=#7ad>Shared with me</color> tab to apply.");
        }

        #endregion

        #region Transient state

        private class BrowsePanel
        {
            public int Page = 1;
            public int Tab = TabMine;            // TabMine | TabPublic | TabShared
            public int? PendingDeleteSlot;
            public DateTime? PendingDeleteArmedUtc;
            public int? PendingRenameSlot;       // when set, that row's name cell renders as an input field
            public string PublicSearchQuery;     // filter for Public tab (matches name + artist)
            public ulong AdminTargetId;          // 0 = browsing self
            // Preview-modal state. PreviewCrc is the FileStorage CRC currently registered for
            // display (zeroed → no preview open). PreviewOwnerId + PreviewSlot identify which
            // slot the preview is showing so the [Apply from preview] button knows what to fire.
            public uint PreviewCrc;
            public ulong PreviewOwnerId;
            public int PreviewSlot;
            // Share-modal state (v0.10.2). When ShareModalSlot > 0, the modal is open
            // for that slot. ShareModalPage paginates the contacts list. ShareModalSearch
            // is the typed filter text.
            public int ShareModalSlot;
            public int ShareModalPage = 1;
            public string ShareModalSearch;
        }

        private class PendingApply
        {
            public int Slot;
            public DateTime ArmedUtc;
            public ulong TargetUserId; // library to read slot from; 0 = self
        }

        // Player armed a USE-key save via the panel toolbar — the click closed the panel
        // and now we wait for them to look at a sign + press USE. Mirrors the awaitingApply
        // pattern.
        private class PendingSave
        {
            public DateTime ArmedUtc;
        }

        // Player clicked Import URL: we captured the target entity at click-time (head ray
        // is still aimed at it because cursor mode doesn't move the player's view) and now
        // we wait for them to type a URL into the modal. The captured entity reference is
        // used by the apply handlers without re-raycasting — by then the cursor has moved
        // to the input field.
        private class PendingImport
        {
            public BaseEntity Entity;
            public string Kind;            // KindSign | KindPhotoFrame | KindPumpkin | KindPaintedItem
            public uint TextureIndex;
            public DateTime ArmedUtc;
        }

        private readonly Dictionary<ulong, BrowsePanel> openPanels = new Dictionary<ulong, BrowsePanel>();
        private readonly Dictionary<ulong, PendingApply> awaitingApply = new Dictionary<ulong, PendingApply>();
        private readonly Dictionary<ulong, PendingSave>  awaitingSave  = new Dictionary<ulong, PendingSave>();
        private readonly Dictionary<ulong, PendingImport> awaitingImport = new Dictionary<ulong, PendingImport>();
        // Per-player "skip the next OnImagePost auto-capture" set. Populated when the user
        // chose Import-URL-Apply-Only; consumed in OnImagePost so a one-off URL paint
        // doesn't take a slot.
        private readonly HashSet<ulong> skipNextCapture = new HashSet<ulong>();
        private readonly Dictionary<ulong, DateTime> applyCooldown = new Dictionary<ulong, DateTime>();
        private readonly Dictionary<ulong, DateTime> wipeArmedUtc = new Dictionary<ulong, DateTime>();
        private readonly HashSet<ulong> warnedLibraryFull = new HashSet<ulong>();
        // Anti-spam: (artistId, recipientId) → last NotifyShareAdded time. Re-sharing the
        // same slot to the same buyer (or unshare+reshare cycles) won't fire a fresh chat
        // notification more than once per cooldown window. In-memory only — server restart
        // clears the table, which is fine since an attacker would need to reconnect anyway.
        private readonly Dictionary<(ulong artist, ulong recipient), DateTime> shareNotifyCooldown
            = new Dictionary<(ulong, ulong), DateTime>();
        private const double ShareNotifyCooldownSeconds = 300.0;
        // Diagnostic timer (v0.11.2). Drives the periodic self-heal sweep when
        // config.SelfHealPeriodicScanMinutes > 0. Off by default — enable while
        // investigating a recurring trigger.
        private Timer selfHealPeriodicTimer;
        // Save-on-change debouncer (v0.11.4). Resets on every StampLib() call so a burst
        // of mutations only triggers one disk write, but the data file is never more than
        // SaveDebounceSeconds stale — closes the save-loss window between OnServerSave
        // ticks (Rust default ~10 min). See StampLib + MarkDirty.
        private Timer _saveDebounceTimer;
        private const float SaveDebounceSeconds = 5f;
        // Idempotency flag for the OnServerInitialized → Init fallback. Hot-reload skips
        // OnServerInitialized so we run the same setup from Init via NextTick — flag stops
        // the cold-start path from also re-running it on hot-reload.
        private bool _postLoadDone;

        private bool _unloading;

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
            // Hot-reload safety net (v0.11.4). Carbon's hot-reload SKIPS OnServerInitialized
            // for late-loaded plugins; only Init fires. Schedule PostLoadSetup via NextTick
            // so the dep graph + config are settled, with an idempotency flag so cold-start
            // (Init → OnServerInitialized) doesn't run setup twice. Saves us from a hot-
            // reload leaving the plugin half-initialized (sign-aim poll missing, self-heal
            // sweep skipped, default-group grant missing).
            NextTick(() =>
            {
                try { if (!_postLoadDone) PostLoadSetup(); }
                catch (Exception e) { PrintError($"Init hot-reload PostLoadSetup threw: {e}"); }
            });
        }

        private void OnServerInitialized()
        {
            try
            {
                if (!_postLoadDone) PostLoadSetup();
            }
            catch (Exception e) { PrintError($"OnServerInitialized threw: {e}"); }
        }

        // Single source of truth for everything that needs to run AFTER LoadData and AFTER
        // the SignArtist plugin reference resolves. Idempotent — guarded by _postLoadDone.
        // Cold start: Init → LoadData → OnServerInitialized → PostLoadSetup.
        // Hot reload: Init → LoadData → NextTick → PostLoadSetup (OnServerInitialized is
        // never fired by Carbon for late-loaded plugins).
        private void PostLoadSetup()
        {
            if (_postLoadDone) return;
            _postLoadDone = true;

            if (SignArtist == null)
            {
                PrintError("Sign Artist plugin not loaded — SignArtSaver requires it. Unloading.");
                Interface.Oxide.UnloadPlugin(Name);
                return;
            }

            string saVersion = SignArtist.Version != null ? SignArtist.Version.ToString() : "?";
            if (SignArtist.Version != null && (SignArtist.Version.Major != 1 || SignArtist.Version.Minor < 4 || SignArtist.Version.Minor > 4))
            {
                PrintWarning($"Sign Artist v{saVersion} is outside the audited 1.4.x range; OnImagePost signature or API_Skin* args may differ and auto-capture/replay may silently miss.");
            }

            if (config.AutoGrantDefaultGroup && !permission.GroupHasPermission("default", PermUse))
            {
                permission.GrantGroupPermission("default", PermUse, this);
                Puts($"Granted {PermUse} to default group.");
            }

            int totalPlayers = store?.Players?.Count ?? 0;
            int totalSlots = store?.Players?.Sum(kv => kv.Value?.Slots?.Count ?? 0) ?? 0;
            Puts($"SignArtSaver v{Version} loaded. Sign Artist v{saVersion} resolved. Library: {totalPlayers} players, {totalSlots} slots, cap {config.SlotsPerPlayer}/player. Auto-capture: {(config.AutoCapture ? "ON" : "OFF")}.");

            // Backfill OwnerName for libraries that pre-date v0.3.0 (the field didn't exist
            // when those slots were saved) by pulling the cached name from Oxide's player
            // records. Fast pass — does nothing for libraries that already have a name.
            int backfilled = 0;
            foreach (var kv in store.Players)
            {
                var lib = kv.Value;
                if (lib == null || !string.IsNullOrEmpty(lib.OwnerName)) continue;
                var iplayer = covalence.Players.FindPlayerById(kv.Key.ToString());
                if (iplayer != null && !string.IsNullOrEmpty(iplayer.Name))
                {
                    lib.OwnerName = iplayer.Name;
                    StampLib(lib);
                    backfilled++;
                }
            }
            if (backfilled > 0) Puts($"Backfilled OwnerName on {backfilled} legacy librar{(backfilled == 1 ? "y" : "ies")}.");

            // Roster backfill (v0.10.1): seed KnownPlayers from existing OwnerName fields
            // and currently online players so /saveart share by name resolves immediately
            // after upgrade — without this the roster only fills as players reconnect.
            int rosterAdded = 0;
            foreach (var kv in store.Players)
            {
                if (string.IsNullOrEmpty(kv.Value?.OwnerName)) continue;
                if (!store.KnownPlayers.ContainsKey(kv.Key))
                {
                    store.KnownPlayers[kv.Key] = new KnownPlayer { Name = kv.Value.OwnerName, LastSeenUtc = kv.Value.LastUpdatedUtc };
                    rosterAdded++;
                }
            }
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !p.userID.IsSteamId() || string.IsNullOrEmpty(p.displayName)) continue;
                if (!store.KnownPlayers.ContainsKey((ulong)p.userID))
                {
                    store.KnownPlayers[(ulong)p.userID] = new KnownPlayer { Name = p.displayName, LastSeenUtc = DateTime.UtcNow };
                    rosterAdded++;
                }
            }
            if (rosterAdded > 0) Puts($"Roster: seeded {rosterAdded} player(s) from existing data + active list (total known: {store.KnownPlayers.Count}).");

            // Backfill canvas info on slots saved before v0.3.0 (those have BytesPath but
            // no OriginalCanvasName / OriginalImageWidth). We read the PNG header (first 24
            // bytes — IHDR chunk has width+height) and reverse-look-up CanvasInfo by
            // dimensions. For ambiguous dimensions (e.g. 512×256 matches both Medium and
            // Large Wooden Sign) we take the first dict entry — best-effort, the user
            // gets a reasonable name + accurate dimensions.
            int canvasBackfilled = 0;
            foreach (var kv in store.Players)
            {
                var lib = kv.Value;
                if (lib?.Slots == null) continue;
                foreach (var art in lib.Slots)
                {
                    if (!string.IsNullOrEmpty(art.OriginalCanvasName) && art.OriginalImageWidth > 0) continue;
                    if (string.IsNullOrEmpty(art.BytesPath)) continue;
                    var fullPath = Path.Combine(Interface.Oxide.DataDirectory, "SignArtSaver", art.BytesPath);
                    if (!File.Exists(fullPath)) continue;
                    try
                    {
                        byte[] header = new byte[24];
                        using (var fs = File.OpenRead(fullPath))
                        {
                            if (fs.Read(header, 0, 24) != 24) continue;
                        }
                        if (!TryReadPngDimensions(header, out int w, out int h)) continue;
                        art.OriginalImageWidth = w;
                        art.OriginalImageHeight = h;
                        if (TryMatchCanvasByDimensions(w, h, out var pf, out var nm))
                        {
                            art.OriginalShortPrefab = pf;
                            art.OriginalCanvasName = nm;
                        }
                        else
                        {
                            art.OriginalCanvasName = $"{art.EntityKind ?? "Sign"} {w}×{h}";
                        }
                        canvasBackfilled++;
                    }
                    catch (Exception e) { PrintWarning($"Canvas backfill for slot {art.Slot} failed: {e.Message}"); }
                }
            }
            if (canvasBackfilled > 0) Puts($"Backfilled canvas info on {canvasBackfilled} slot(s) from PNG dimensions.");

            // Self-heal sweep for everyone already online at plugin-load (v0.11.0). Without
            // this the scan only runs on the next reconnect — annoying after a hot-reload
            // when you're actively investigating a blank sign. NextTick to defer one frame.
            if (config != null && config.SelfHealEnabled)
            {
                NextTick(() =>
                {
                    foreach (var p in BasePlayer.activePlayerList)
                    {
                        if (p == null || !p.userID.IsSteamId()) continue;
                        try { RunSelfHealForApplier(p); }
                        catch (Exception e) { PrintWarning($"[SelfHeal] Initial sweep for {p.displayName} threw: {e.Message}"); }
                    }
                });
            }

            // Diagnostic periodic scan (v0.11.2). Off by default; opt in via config when
            // investigating a recurring blank-art trigger.
            StartSelfHealPeriodicScanIfEnabled();
        }

        private void Unload()
        {
            _unloading = true;
            try
            {
                selfHealPeriodicTimer?.Destroy();
                selfHealPeriodicTimer = null;
                // Cancel any pending debounced save — Unload() calls SaveData() below
                // synchronously to flush whatever the debouncer was holding.
                _saveDebounceTimer?.Destroy();
                _saveDebounceTimer = null;
                foreach (var kv in new Dictionary<ulong, BrowsePanel>(openPanels))
                {
                    var p = BasePlayer.FindByID(kv.Key);
                    // Free any registered FileStorage preview entries before tearing down CUI.
                    ClosePreview(p, kv.Value, refreshPanel: false);
                    if (p != null && p.IsConnected) DestroyAllUi(p);
                }
                openPanels.Clear();
                awaitingApply.Clear();
                awaitingSave.Clear();
                awaitingImport.Clear();
                skipNextCapture.Clear();
                applyCooldown.Clear();
                wipeArmedUtc.Clear();
                warnedLibraryFull.Clear();
                shareNotifyCooldown.Clear();
                SaveData();
            }
            catch (Exception e) { PrintError($"Unload error: {e.Message}"); }
        }

        private void OnServerSave()
        {
            try { SaveData(); }
            catch (Exception e) { PrintError($"OnServerSave threw: {e}"); }
        }

        #endregion

        #region Hook handlers

        // Sign Artist fires OnImagePost AFTER QueueDownload accepts the request but BEFORE
        // its DownloadImage coroutine has actually finished + stored the bytes — QueueDownload
        // enqueues + StartCoroutines DownloadImage, the first `yield return` in that coroutine
        // is a web request (HEAD or GET) which runs before FileStorage.Store, and the
        // CallHook("OnImagePost", ...) fires immediately after QueueDownload returns. So if
        // we read entity.textureIDs[i] here it returns the PREVIOUS CRC, not the new one;
        // byte-mode auto-capture would silently store stale-or-zero bytes. Fixed in v0.11.4
        // by snapshotting the pre-CRC and polling for it to change before reading bytes.
        // Signature: (BasePlayer, string url, bool raw, BaseEntity entity, uint textureIndex)
        private void OnImagePost(BasePlayer player, string url, bool raw, BaseEntity entity, uint textureIndex)
        {
            try
            {
                if (_unloading) return;
                if (config == null || !config.AutoCapture) return;
                if (player == null || entity == null || string.IsNullOrEmpty(url)) return;
                if (!player.userID.IsSteamId()) return;

                // One-shot bypass: Import-URL-Apply-Only flow set this flag before calling
                // SignArtist's API_SkinSign so the resulting OnImagePost doesn't grab a slot.
                if (skipNextCapture.Remove((ulong)player.userID)) return;

                string kind = ResolveKind(entity);
                if (kind == null) return;

                // Auto-capture only saves the artist's own work. Admins want explicit /saveart admin
                // for capturing other players' signs to keep the data file clean.
                if (config.RequireOwnerSave && entity.OwnerID != player.userID) return;

                if (UrlIsBlocked(url))
                {
                    player.ChatMessage(Tag() + Err(L("UrlBlocked", player)));
                    return;
                }

                // Snapshot the pre-paint CRC + start the deferred poll. On CRC change we
                // read the fresh bytes and call CaptureToLibrary; on timeout we capture
                // URL-only so the artist at least has a recoverable reference. Up to
                // ~5 seconds of polling (10 × 0.5s) — well under Sign Artist's 10s download
                // ceiling but generous enough for typical CDNs.
                uint preCrc = ReadEntityCrc(entity, kind, textureIndex);
                DeferredByteCapture(player, (ulong)player.userID, url, raw, kind, textureIndex, entity, preCrc, attemptsRemaining: 10);
            }
            catch (Exception e) { PrintError($"OnImagePost threw: {e}"); }
        }

        // Retry-poll that watches entity.CRC for Sign Artist's download coroutine to land
        // its FileStorage.Store. Captures bytes on change, URL-only on timeout. Resilient
        // to the entity being destroyed mid-poll (capture URL-only against a null entity).
        private void DeferredByteCapture(
            BasePlayer caller, ulong libUserId, string url, bool raw, string kind,
            uint texIdx, BaseEntity entity, uint preCrc, int attemptsRemaining)
        {
            if (_unloading) return;
            if (entity == null || entity.IsDestroyed)
            {
                // Sign disappeared (pickup, decay, etc). Save URL-only on the artist's
                // library so the reference isn't lost; bytes can be backfilled later via
                // /saveart save while aimed.
                try { CaptureToLibrary(caller, libUserId, url, raw, kind, texIdx, null, null, explicitName: null, autoCaptured: true); }
                catch (Exception e) { PrintError($"DeferredByteCapture URL-only fallback threw: {e}"); }
                return;
            }
            uint currentCrc = ReadEntityCrc(entity, kind, texIdx);
            if (currentCrc != 0 && currentCrc != preCrc)
            {
                // CRC flipped — Sign Artist's FileStorage.Store landed. Fetch the bytes
                // (now actually available) and complete the capture.
                byte[] bytes = null;
                try { bytes = FetchPngFromEntity(entity, texIdx); }
                catch (Exception e) { PrintWarning($"FetchPngFromEntity (post-CRC-flip) threw: {e.Message}"); }
                try { CaptureToLibrary(caller, libUserId, url, raw, kind, texIdx, bytes, entity, explicitName: null, autoCaptured: true); }
                catch (Exception e) { PrintError($"CaptureToLibrary (post-CRC-flip) threw: {e}"); }
                return;
            }
            if (attemptsRemaining <= 0)
            {
                // Gave up waiting. Common in the rare same-image-different-URL case where
                // Sign Artist's resulting CRC matches the previous paint. Slot is still
                // useful URL-only — apply paths fall back to URL replay on own-library.
                try { CaptureToLibrary(caller, libUserId, url, raw, kind, texIdx, null, entity, explicitName: null, autoCaptured: true); }
                catch (Exception e) { PrintError($"CaptureToLibrary (timeout URL-only) threw: {e}"); }
                return;
            }
            timer.Once(0.5f, () =>
            {
                try { DeferredByteCapture(caller, libUserId, url, raw, kind, texIdx, entity, preCrc, attemptsRemaining - 1); }
                catch (Exception e) { PrintError($"DeferredByteCapture re-entry threw: {e}"); }
            });
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            try { OnPlayerInputImpl(player, input); }
            catch (Exception e) { PrintError($"OnPlayerInput threw: {e}"); }
        }

        private void OnPlayerInputImpl(BasePlayer player, InputState input)
        {
            if (_unloading) return;
            if (player == null || input == null) return;
            if (!input.WasJustPressed(BUTTON.USE)) return;

            // Armed save (toolbar Save sign button → close panel → look at sign → USE).
            // Checked first; clicking Save in the panel implicitly cancelled any prior apply.
            if (awaitingSave.TryGetValue(player.userID, out var pendingSave))
            {
                if ((DateTime.UtcNow - pendingSave.ArmedUtc).TotalSeconds > config.PendingApplyTimeoutSeconds)
                {
                    awaitingSave.Remove(player.userID);
                    player.ChatMessage(Tag() + Warn(L("SaveTimedOut", player)));
                    return;
                }
                awaitingSave.Remove(player.userID);

                if (!TryRaycastSign(player, out var sEntity, out var sKind, out var sTexIdx))
                {
                    player.ChatMessage(Tag() + Err(L("NoSignInAim", player)));
                    return;
                }
                if (config.RequireOwnerSave && sEntity.OwnerID != player.userID && !HasAdmin(player))
                {
                    player.ChatMessage(Tag() + Err(L("NotOwner", player)));
                    return;
                }
                byte[] sBytes = FetchPngFromEntity(sEntity, sTexIdx);
                if (sBytes == null || sBytes.Length == 0)
                {
                    player.ChatMessage(Tag() + Err(L("NoImageOnSign", player)));
                    return;
                }
                CaptureToLibrary(player, (ulong)player.userID, url: null, raw: false, kind: sKind, texIdx: sTexIdx, bytes: sBytes, entity: sEntity, explicitName: null, autoCaptured: false);
                return;
            }

            if (!awaitingApply.TryGetValue(player.userID, out var pending)) return;

            if ((DateTime.UtcNow - pending.ArmedUtc).TotalSeconds > config.PendingApplyTimeoutSeconds)
            {
                awaitingApply.Remove(player.userID);
                player.ChatMessage(Tag() + Warn(L("ApplyTimedOut", player)));
                return;
            }

            if (!TryRaycastSign(player, out var entity, out var kind, out _)) return;

            ulong libUserId = pending.TargetUserId != 0 ? pending.TargetUserId : (ulong)player.userID;
            var lib = store.Players.TryGetValue(libUserId, out var l) ? l : null;
            var art = lib?.Slots.FirstOrDefault(s => s.Slot == pending.Slot);
            if (art == null)
            {
                awaitingApply.Remove(player.userID);
                player.ChatMessage(Tag() + Err($"Slot {pending.Slot} no longer exists."));
                return;
            }

            awaitingApply.Remove(player.userID);
            ApplyArtToEntity(player, entity, kind, art, libUserId);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            try
            {
                if (player == null) return;
                // Free preview FileStorage entry before dropping panel state.
                if (openPanels.TryGetValue(player.userID, out var panel))
                    ClosePreview(player, panel, refreshPanel: false);
                DestroyAllUi(player);
                openPanels.Remove(player.userID);
                awaitingApply.Remove(player.userID);
                awaitingSave.Remove(player.userID);
                awaitingImport.Remove(player.userID);
                skipNextCapture.Remove(player.userID);
                applyCooldown.Remove(player.userID);
                wipeArmedUtc.Remove(player.userID);
                warnedLibraryFull.Remove(player.userID);
            }
            catch (Exception e) { PrintError($"OnPlayerDisconnected threw: {e}"); }
        }

        // Cache the player's display name on connect AND wake-up so the public gallery can
        // attribute art to the artist by name (not just steamid). Cheap, idempotent. Also
        // stamps the server-wide KnownPlayers roster (v0.10.1) so /saveart share by name
        // resolves to a steamid even for players who haven't published any art.
        private void OnPlayerConnected(BasePlayer player)
        {
            try
            {
                StampOwnerName(player);
                // Defer the self-heal scan one tick so the player's networking is fully wired
                // (entities will be in BaseNetworkable.serverEntities either way, but holding off
                // a frame avoids racing with anything else hooking OnPlayerConnected).
                if (config != null && config.SelfHealEnabled)
                    NextTick(() =>
                    {
                        try { RunSelfHealForApplier(player); }
                        catch (Exception e) { PrintError($"OnPlayerConnected self-heal threw: {e}"); }
                    });
            }
            catch (Exception e) { PrintError($"OnPlayerConnected threw: {e}"); }
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            try { StampOwnerName(player); }
            catch (Exception e) { PrintError($"OnPlayerSleepEnded threw: {e}"); }
        }

        private void StampOwnerName(BasePlayer player)
        {
            if (_unloading || player == null) return;
            if (!player.userID.IsSteamId()) return;
            ulong uid = (ulong)player.userID;
            string name = player.displayName;
            if (!string.IsNullOrEmpty(name))
            {
                StampRoster(uid, name);
                if (store.Players.TryGetValue(uid, out var lib) && lib.OwnerName != name)
                {
                    lib.OwnerName = name;
                    StampLib(lib);
                }
            }
        }

        // Roster upsert. Idempotent and cheap. Called from connect/wake hooks plus the
        // share-recipient path so a player typed by exact steamid still gets a name we can
        // display to the artist.
        private void StampRoster(ulong steamId, string name)
        {
            if (string.IsNullOrEmpty(name) || !steamId.IsSteamId()) return;
            if (!store.KnownPlayers.TryGetValue(steamId, out var entry))
            {
                entry = new KnownPlayer();
                store.KnownPlayers[steamId] = entry;
            }
            entry.Name = name;
            entry.LastSeenUtc = DateTime.UtcNow;
        }

        #endregion

        #region Capture / apply

        // Append a slot to libUserId's library. Either URL or bytes (or both) must be
        // non-null. Dedupe priority: bytes-sha1 > url-hash. Honours slot cap.
        // Caller is the BasePlayer running the operation (chat feedback); libUserId is the
        // library being modified (differs from caller for /saveart admin). entity is the
        // source sign (used to stamp OriginalShortPrefab + canvas dimensions).
        private SavedArt CaptureToLibrary(
            BasePlayer caller,
            ulong libUserId,
            string url,
            bool raw,
            string kind,
            uint texIdx,
            byte[] bytes,
            BaseEntity entity,
            string explicitName,
            bool autoCaptured)
        {
            var lib = GetOrCreate(libUserId);
            string urlHash = !string.IsNullOrEmpty(url) ? UrlHashKey(url) : null;
            string bytesHash = bytes != null && bytes.Length > 0 ? ByteSha1(bytes) : null;

            // Dedupe: bytes-hash beats url-hash since bytes are the canonical content.
            var dup = lib.Slots.FirstOrDefault(s =>
                s.EntityKind == kind && s.TextureIndex == texIdx &&
                ((bytesHash != null && s.BytesSha1 == bytesHash) ||
                 (bytesHash == null && urlHash != null && s.UrlHash == urlHash)));
            if (dup != null)
            {
                if (!autoCaptured && caller != null && caller.IsConnected)
                    caller.ChatMessage(Tag() + Warn(L("AlreadySavedDup", caller, dup.Slot, dup.Name)));
                return null;
            }

            // Defense-in-depth disk-fill guard (v0.11.7). Rust's engine already caps PNG
            // uploads near 2MB; this enforces an explicit per-slot ceiling so a future
            // engine change or a manual byte-mode save of a misformatted blob can't
            // balloon disk usage. Slot is dropped with a warning on overflow.
            if (bytes != null && bytes.Length > config.MaxBytesPerSlot)
            {
                if (caller != null && caller.IsConnected)
                    caller.ChatMessage(Tag() + Warn($"Image too large ({bytes.Length / 1024}KB > cap {config.MaxBytesPerSlot / 1024}KB). Not saved."));
                else
                    PrintWarning($"[slot capture] Refused {bytes.Length / 1024}KB payload — exceeds MaxBytesPerSlot ({config.MaxBytesPerSlot / 1024}KB).");
                return null;
            }

            if (lib.Slots.Count >= config.SlotsPerPlayer)
            {
                if (autoCaptured)
                {
                    if (caller != null && caller.IsConnected && !warnedLibraryFull.Contains((ulong)caller.userID))
                    {
                        caller.ChatMessage(Tag() + Warn(L("LibraryFullAuto", caller, lib.Slots.Count, config.SlotsPerPlayer)));
                        warnedLibraryFull.Add((ulong)caller.userID);
                    }
                }
                else if (caller != null && caller.IsConnected)
                {
                    caller.ChatMessage(Tag() + Err(L("LibraryFull", caller, lib.Slots.Count, config.SlotsPerPlayer)));
                }
                return null;
            }

            int slot = lib.NextSlotId++;
            string name = SanitizeSlotName(!string.IsNullOrEmpty(explicitName) ? explicitName : $"art-{slot}");
            if (name.Length > 32) name = name.Substring(0, 32);
            if (string.IsNullOrEmpty(name)) name = $"art-{slot}";

            string shortPrefab = entity?.ShortPrefabName;
            CanvasInfo.TryGetValue(shortPrefab ?? "", out var canvasInfo);

            var art = new SavedArt
            {
                Slot = slot,
                Name = name,
                Url = url,
                UrlHash = urlHash,
                EntityKind = kind,
                TextureIndex = texIdx,
                Raw = raw,
                SavedUtc = DateTime.UtcNow,
                OriginalShortPrefab = shortPrefab,
                OriginalCanvasName = canvasInfo.Name ?? PrettifyShortPrefab(shortPrefab),
                OriginalImageWidth = canvasInfo.W,
                OriginalImageHeight = canvasInfo.H,
            };

            // Persist PNG file if bytes provided. Slot stays URL-only if file write fails.
            if (bytesHash != null)
            {
                if (TryWritePngFile(libUserId, slot, bytes, out var werr))
                {
                    art.BytesPath = ImageRelativePath(libUserId, slot);
                    art.BytesSha1 = bytesHash;
                    art.BytesSize = bytes.Length;
                }
                else
                {
                    PrintWarning($"[slot {slot}] Failed to write PNG file: {werr}. Slot saved with URL only.");
                }
            }

            lib.Slots.Add(art);
            StampLib(lib);

            // Self-heal tracking on capture (v0.11.0). The art is on `entity` right now; if it
            // ever blanks server-side, the connecting player (the capturer) is the natural
            // defender. Only meaningful for byte-mode slots — URL-only slots have nothing
            // local to re-write from, so self-heal can't repair them.
            if (caller != null && entity != null && entity.net != null && !string.IsNullOrEmpty(art.BytesPath))
            {
                try
                {
                    RecordAppliedEntity(art, entity, (ulong)caller.userID, kind, texIdx);
                    if (store.Players.TryGetValue((ulong)caller.userID, out var capLib))
                        RemoveStaleAppliedRecordsInLib(capLib, entity.net.ID.Value, texIdx, art);
                }
                catch (Exception e) { PrintWarning($"[SelfHeal] Capture-tracking on slot {art.Slot} threw: {e.Message}"); }
            }

            if (caller != null && caller.IsConnected)
            {
                string label = autoCaptured ? "Auto-saved" : "Saved";
                caller.ChatMessage(Tag() + Ok($"{label} as slot {slot} (\"{name}\", {kind}). /saveart to browse."));
                if (config.WarnOnDiscordCdn && !string.IsNullOrEmpty(url) && IsDiscordCdn(url))
                {
                    string note = bytesHash != null
                        ? "Discord CDN URLs expire in ~24h, but the bytes are saved locally and survive."
                        : "Discord CDN URLs expire in ~24h. For wipe-survival host on imgur / GitHub raw / your own server.";
                    caller.ChatMessage(Tag() + Warn(note));
                }
            }
            return art;
        }

        // Apply a saved slot to the given entity. Honours kind-strict + ownership + cooldown
        // checks. Reaches Sign Artist via the right API_* method per kind.
        // slotOwnerId is the SteamID of the library the slot was looked up from — used to
        // tell own-library applies from cross-library (public gallery, shared-with-me, admin
        // browsing). Cross-library URL fallback is refused entirely: a missing bytes file
        // on a public slot would otherwise issue an artist-controlled URL fetch from the
        // buyer's server (SSRF).
        private void ApplyArtToEntity(BasePlayer caller, BaseEntity entity, string targetKind, SavedArt art, ulong slotOwnerId)
        {
            if (caller == null || entity == null || art == null) return;
            bool isOwnSlot = slotOwnerId == (ulong)caller.userID;

            if (config.RequireOwnerApply && entity.OwnerID != caller.userID && !HasAdmin(caller))
            {
                caller.ChatMessage(Tag() + Err(L("NotOwner", caller)));
                return;
            }

            if (config.StrictKindMatch && art.EntityKind != targetKind)
            {
                caller.ChatMessage(Tag() + Err($"Slot {art.Slot} was saved for a {art.EntityKind}; can't apply to a {targetKind}."));
                return;
            }

            if (applyCooldown.TryGetValue(caller.userID, out var lastUtc))
            {
                double remaining = config.ApplyCooldownSeconds - (DateTime.UtcNow - lastUtc).TotalSeconds;
                if (remaining > 0)
                {
                    caller.ChatMessage(Tag() + Warn(L("CooldownActive", caller, remaining)));
                    return;
                }
            }
            applyCooldown[caller.userID] = DateTime.UtcNow;

            // Prefer byte-mode replay (no network round-trip; works even if the URL is dead).
            if (!string.IsNullOrEmpty(art.BytesPath))
            {
                var fullPath = Path.Combine(Interface.Oxide.DataDirectory, "SignArtSaver", art.BytesPath);
                if (File.Exists(fullPath))
                {
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(fullPath); }
                    catch (Exception e)
                    {
                        PrintWarning($"[slot {art.Slot}] PNG read failed ({e.Message}); will try URL.");
                        bytes = null;
                    }

                    if (bytes != null && bytes.Length > 0)
                    {
                        // Auto-resize to target canvas dimensions if known. Sign Artist's URL
                        // pipeline does the equivalent during /sil; we mirror it here so a
                        // saved 256×192 picture frame applied to a 512×512 XL frame doesn't
                        // render stretched.
                        byte[] applyBytes = bytes;
                        var targetSize = LookupCanvasSize(entity?.ShortPrefabName);
                        string resizeNote = "";
                        if (targetSize.HasValue)
                        {
                            var (tw, th) = targetSize.Value;
                            if (TryResizePngBytes(bytes, tw, th, out var resized, out var rerr))
                            {
                                if (resized != null && resized.Length > 0 && !ReferenceEquals(resized, bytes))
                                {
                                    applyBytes = resized;
                                    resizeNote = $", resized→{tw}×{th}";
                                }
                            }
                            else
                            {
                                PrintWarning($"[slot {art.Slot}] resize to {tw}×{th} failed ({rerr}); applying original bytes.");
                            }
                        }

                        if (ApplyBytesToEntity(entity, targetKind, art.TextureIndex, applyBytes, out var berr))
                        {
                            // Self-heal tracking (v0.11.0). Record the entity on whichever
                            // slot owns it for this applier — for shared/public art, that's a
                            // clone in the applier's library so the tracking survives the
                            // original artist deleting their slot.
                            TrackAppliedEntityForCaller(caller, art, entity, targetKind, art.TextureIndex);
                            caller.ChatMessage(Tag() + Ok(L("SlotApplied", caller, art.Slot, EscapeRich(art.Name), resizeNote)));
                            return;
                        }
                        PrintWarning($"[slot {art.Slot}] Byte-mode apply failed: {berr}; will try URL.");
                    }
                }
                else
                {
                    PrintWarning($"[slot {art.Slot}] PNG file missing at {fullPath}; will try URL.");
                }
            }

            // URL fallback (or primary, if no bytes were ever captured).
            // Security: cross-library applies never fall through to a URL fetch. A hostile
            // artist could otherwise publish a slot pointing at an internal hostname and
            // wait for a victim's bytes file to be deleted; this guards that vector.
            if (!isOwnSlot)
            {
                caller.ChatMessage(Tag() + Err(L("BytesMissing", caller, art.Slot)));
                return;
            }
            if (string.IsNullOrEmpty(art.Url))
            {
                caller.ChatMessage(Tag() + Err(L("NoUsableData", caller, art.Slot)));
                return;
            }

            if (SignArtist == null)
            {
                caller.ChatMessage(Tag() + Err(L("SignArtistOffline", caller)));
                return;
            }

            // Re-vet the URL with the apply-time guard (rejects IP literals, private
            // hostnames, scheme weirdness). Substring blocklist is too weak on its own;
            // the stricter check matters here because the URL is fetched server-side.
            if (!IsUrlSafeForApply(art.Url, out var urlErr))
            {
                caller.ChatMessage(Tag() + Err(L("UrlBlockedAtApply", caller, art.Slot, urlErr)));
                return;
            }

            try
            {
                if (targetKind == KindSign && entity is Signage sign)
                    SignArtist.Call("API_SkinSign", caller, sign, art.Url, art.Raw, art.TextureIndex);
                else if (targetKind == KindPhotoFrame && entity is PhotoFrame frame)
                    SignArtist.Call("API_SkinPhotoFrame", caller, frame, art.Url, art.Raw);
                else if (targetKind == KindPumpkin && entity is CarvablePumpkin pumpkin)
                    SignArtist.Call("API_SkinPumpkin", caller, pumpkin, art.Url, art.Raw);
                else if (targetKind == KindPaintedItem)
                {
                    // Sign Artist v1.4.6 has no API_Skin* for PaintedItemStorageEntity. Byte-mode
                    // (the path above this fallback) handles it — but a URL-only slot has no
                    // local bytes to use. Tell the player to re-save once with bytes.
                    caller.ChatMessage(Tag() + Err($"Slot {art.Slot} is URL-only; PaintedItem entities (drawable windows etc.) need a byte-mode save. /saveart save while looking at the painted entity."));
                    return;
                }
                else
                {
                    caller.ChatMessage(Tag() + Err("Internal: kind/entity mismatch on dispatch."));
                    return;
                }
            }
            catch (Exception e)
            {
                PrintWarning($"Apply via Sign Artist threw: {e.Message}");
                caller.ChatMessage(Tag() + Err("Apply failed — Sign Artist threw. Check server log."));
                return;
            }

            caller.ChatMessage(Tag() + Ok(L("SlotAppliedUrl", caller, art.Slot, EscapeRich(art.Name))));
        }

        #endregion

        #region Self-heal (v0.11.0)

        // Read the current texture CRC off a painted entity for self-heal verification.
        // Returns 0 for "no art" (entity cleared) OR for invalid args. Distinguishing those
        // cases isn't useful for our purposes — both mean "not what was applied."
        private uint ReadEntityCrc(BaseEntity entity, string kind, uint textureIndex)
        {
            if (entity == null) return 0;
            try
            {
                if (kind == KindSign && entity is Signage sign)
                {
                    sign.EnsureInitialized();
                    if (sign.textureIDs == null) return 0;
                    if (textureIndex >= sign.textureIDs.Length) return 0;
                    return sign.textureIDs[textureIndex];
                }
                if (kind == KindPhotoFrame && entity is PhotoFrame frame)
                    return frame._overlayTextureCrc;
                if (kind == KindPumpkin && entity is CarvablePumpkin pumpkin)
                {
                    if (pumpkin.textureIDs == null) return 0;
                    if (textureIndex >= pumpkin.textureIDs.Length) return 0;
                    return pumpkin.textureIDs[textureIndex];
                }
                if (kind == KindPaintedItem && entity is PaintedItemStorageEntity painted)
                {
                    // Public IUGCBrowserEntity.GetContentCRCs is robust against private field
                    // renames — prefer it over reflection here. Returns a single-element array
                    // for this entity class.
                    var crcs = painted.GetContentCRCs;
                    return (crcs != null && crcs.Length > 0) ? crcs[0] : 0u;
                }
            }
            catch { }
            return 0;
        }

        // Read the cached PNG bytes for a slot off disk. Returns null on any error.
        private byte[] LoadSlotBytes(SavedArt art)
        {
            if (art == null || string.IsNullOrEmpty(art.BytesPath)) return null;
            try
            {
                var fullPath = Path.Combine(Interface.Oxide.DataDirectory, "SignArtSaver", art.BytesPath);
                return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
            }
            catch { return null; }
        }

        // Reverse-lookup: which library does this SavedArt belong to? Used so the apply path
        // can decide whether the apply is shared (applier != owner) without changing every
        // caller's signature. O(N_libraries × N_slots) — fine at human scales (e.g. 50 × 50).
        private ulong? FindSlotOwner(SavedArt art)
        {
            if (art == null || store?.Players == null) return null;
            foreach (var kv in store.Players)
            {
                if (kv.Value?.Slots == null) continue;
                foreach (var s in kv.Value.Slots)
                    if (ReferenceEquals(s, art)) return kv.Key;
            }
            return null;
        }

        // Clone a shared slot into the applier's own library. Idempotent: if the applier
        // already has a slot with matching BytesSha1, returns that one (no duplicate). The
        // clone is independent — when the original artist deletes their slot, the buyer's
        // copy survives so self-heal can still restore from it. Returns null on failure.
        private SavedArt CloneSlotForApplier(ulong applierId, SavedArt sourceArt, out string error)
        {
            error = null;
            if (sourceArt == null) { error = "source slot null"; return null; }
            if (string.IsNullOrEmpty(sourceArt.BytesPath)) { error = "source slot has no bytes (URL-only — can't clone for self-heal)"; return null; }

            var applierLib = GetOrCreate(applierId);

            // Reuse if applier already has a slot with the same bytes (sha1 collision-free at
            // these payload sizes; we use a 10-char prefix but the dedupe is only an
            // optimization — a missed dedupe just costs an extra slot, no correctness issue).
            if (!string.IsNullOrEmpty(sourceArt.BytesSha1))
            {
                var existing = applierLib.Slots.FirstOrDefault(s =>
                    s != null && s.BytesSha1 == sourceArt.BytesSha1 && !string.IsNullOrEmpty(s.BytesPath));
                if (existing != null) return existing;
            }

            if (applierLib.Slots.Count >= config.SlotsPerPlayer)
            {
                error = $"applier library full ({config.SlotsPerPlayer} slots)";
                return null;
            }

            byte[] bytes = LoadSlotBytes(sourceArt);
            if (bytes == null || bytes.Length == 0) { error = "source bytes file missing or empty"; return null; }

            int newSlotId = applierLib.NextSlotId++;
            if (!TryWritePngFile(applierId, newSlotId, bytes, out var werr))
            {
                error = $"write png failed: {werr}";
                applierLib.NextSlotId--; // refund the id we didn't end up using
                return null;
            }

            var clone = new SavedArt
            {
                Slot = newSlotId,
                Name = string.IsNullOrEmpty(sourceArt.Name) ? "(shared)" : sourceArt.Name + " (shared)",
                Url = sourceArt.Url,
                UrlHash = sourceArt.UrlHash,
                Raw = sourceArt.Raw,
                BytesPath = ImageRelativePath(applierId, newSlotId),
                BytesSha1 = sourceArt.BytesSha1,
                BytesSize = bytes.Length,
                EntityKind = sourceArt.EntityKind,
                TextureIndex = sourceArt.TextureIndex,
                SavedUtc = DateTime.UtcNow,
                OriginalShortPrefab = sourceArt.OriginalShortPrefab,
                OriginalCanvasName = sourceArt.OriginalCanvasName,
                OriginalImageWidth = sourceArt.OriginalImageWidth,
                OriginalImageHeight = sourceArt.OriginalImageHeight,
                IsPublic = false,
                PublishedUtc = null,
                SharedWith = new List<ShareEntry>(),
                AppliedEntities = new List<AppliedEntity>(),
            };
            applierLib.Slots.Add(clone);
            StampLib(applierLib);
            return clone;
        }

        // Add or refresh an AppliedEntity record on the slot. If the same NetId+TextureIndex
        // already exists, updates timestamps in place rather than duplicating.
        private void RecordAppliedEntity(SavedArt slot, BaseEntity entity, ulong appliedById, string applyKind, uint textureIndex)
        {
            if (slot == null || entity == null || entity.net == null) return;
            if (slot.AppliedEntities == null) slot.AppliedEntities = new List<AppliedEntity>();

            ulong netId = entity.net.ID.Value;
            var existing = slot.AppliedEntities.FirstOrDefault(ae => ae.NetId == netId && ae.TextureIndex == textureIndex);
            var nowUtc = DateTime.UtcNow;
            if (existing != null)
            {
                existing.AppliedByUserId = appliedById;
                existing.ApplyKind = applyKind;
                existing.AppliedUtc = nowUtc;
                existing.LastVerifiedUtc = nowUtc;
                return;
            }
            slot.AppliedEntities.Add(new AppliedEntity
            {
                NetId = netId,
                AppliedByUserId = appliedById,
                ApplyKind = applyKind,
                TextureIndex = textureIndex,
                AppliedUtc = nowUtc,
                LastVerifiedUtc = nowUtc,
                LastRepairedUtc = null,
                RepairCount = 0,
            });
        }

        // When an entity is overwritten with a different slot's art, the OLD slot still has
        // a stale AppliedEntity record. Walk the applier's library and drop any record
        // pointing at this NetId+TextureIndex that lives on a different slot. Cheap — runs
        // once per apply, scoped to one library.
        private void RemoveStaleAppliedRecordsInLib(PlayerLibrary applierLib, ulong netId, uint textureIndex, SavedArt keepSlot)
        {
            if (applierLib?.Slots == null) return;
            foreach (var s in applierLib.Slots)
            {
                if (s == null || ReferenceEquals(s, keepSlot)) continue;
                if (s.AppliedEntities == null) continue;
                s.AppliedEntities.RemoveAll(ae => ae.NetId == netId && ae.TextureIndex == textureIndex);
            }
        }

        // Called from the apply-success path. Decides whether the apply is shared (applier
        // != slot owner) and clones bytes into the applier's library if so, then writes the
        // AppliedEntity record onto whichever slot ends up tracking the apply. Best-effort —
        // if anything fails we log and let the apply succeed regardless. Self-heal not having
        // a record for some entity is strictly less harmful than refusing to apply.
        private void TrackAppliedEntityForCaller(BasePlayer caller, SavedArt sourceArt, BaseEntity entity, string applyKind, uint textureIndex)
        {
            if (caller == null || sourceArt == null || entity == null || entity.net == null) return;

            try
            {
                ulong applierId = (ulong)caller.userID;
                ulong? ownerOpt = FindSlotOwner(sourceArt);
                if (!ownerOpt.HasValue)
                {
                    // Slot isn't in any library? Shouldn't happen — log and bail without tracking.
                    PrintWarning($"[SelfHeal] Could not resolve slot owner for slot {sourceArt.Slot}; skipping apply tracking.");
                    return;
                }
                ulong ownerId = ownerOpt.Value;

                SavedArt trackingSlot = sourceArt;
                if (config.SelfHealCloneSharedBytes && applierId != ownerId)
                {
                    var clone = CloneSlotForApplier(applierId, sourceArt, out var cerr);
                    if (clone != null)
                    {
                        trackingSlot = clone;
                    }
                    else
                    {
                        // If cloning fails (library full, disk full), fall back to recording
                        // on the source slot. Self-heal will still scan it on the OWNER's
                        // connect — not the applier's — so it's a degraded but functional state.
                        PrintWarning($"[SelfHeal] Clone for shared apply failed ({cerr}); recording AppliedEntity on source slot {sourceArt.Slot} owned by {ownerId} instead. Buyer's art will only self-heal when the artist logs in.");
                    }
                }

                ulong netId = entity.net.ID.Value;
                RecordAppliedEntity(trackingSlot, entity, applierId, applyKind, textureIndex);

                // Drop stale records that point at this same entity from OTHER slots in the
                // applier's library (e.g. they used to apply slot 3 here, now applying slot 7).
                if (store.Players.TryGetValue(applierId, out var applierLib))
                    RemoveStaleAppliedRecordsInLib(applierLib, netId, textureIndex, trackingSlot);
            }
            catch (Exception e)
            {
                PrintWarning($"[SelfHeal] Tracking apply for slot {sourceArt.Slot} threw (apply itself succeeded): {e.Message}");
            }
        }

        // The actual scan. Walk every slot in the connecting player's own library and verify
        // every AppliedEntity record where AppliedByUserId == this player. Repair via the
        // safe ApplyBytesToEntity path on broken state. Side effect: drops records pointing
        // at destroyed entities (legitimate decay/raid/pickup — not a bug to repair).
        private void RunSelfHealForApplier(BasePlayer player)
        {
            if (!config.SelfHealEnabled) return;
            if (player == null || !player.userID.IsSteamId()) return;
            if (!store.Players.TryGetValue((ulong)player.userID, out var lib) || lib?.Slots == null) return;

            int verified = 0, repaired = 0, dropped = 0, skipped = 0, failed = 0;
            var nowUtc = DateTime.UtcNow;
            ulong applierId = (ulong)player.userID;

            foreach (var art in lib.Slots)
            {
                if (art?.AppliedEntities == null) continue;
                // Iterate backwards because we mutate the list when dropping stale records.
                for (int i = art.AppliedEntities.Count - 1; i >= 0; i--)
                {
                    var ae = art.AppliedEntities[i];
                    if (ae == null || ae.AppliedByUserId != applierId) continue;

                    var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(ae.NetId)) as BaseEntity;
                    if (entity == null || entity.IsDestroyed)
                    {
                        // Entity is gone for legitimate reasons (decay, raid, picked up).
                        // Drop the tracking record so we don't try to repair a corpse.
                        art.AppliedEntities.RemoveAt(i);
                        dropped++;
                        if (config.SelfHealVerboseLogging)
                            Puts($"[SelfHeal] DROP slot={art.Slot} (\"{art.Name}\") entity={ae.NetId} ({ae.ApplyKind}) — entity destroyed (decay/raid/pickup).");
                        continue;
                    }

                    // Ownership re-check. The entity could have been picked up and
                    // re-deployed by a different player (different ownership; same NetID does
                    // NOT survive pickup, but base-claim / ownership-transfer plugins exist).
                    // If the current entity owner no longer matches the original applier, the
                    // record is stale — silently repainting someone else's sign would be a
                    // post-raid griefing vector. Drop the record; the applier can re-arm if
                    // they regain legitimate access. This is a safety property, NOT a permission
                    // policy — it runs unconditionally regardless of RequireOwnerApply, which
                    // only gates manual /saveart apply (a friendly server may allow non-owners
                    // to apply, but heal-time silent repaint of a different owner's sign is
                    // never the intended outcome).
                    if (entity.OwnerID != ae.AppliedByUserId)
                    {
                        art.AppliedEntities.RemoveAt(i);
                        dropped++;
                        if (config.SelfHealVerboseLogging)
                            Puts($"[SelfHeal] DROP slot={art.Slot} (\"{art.Name}\") entity={ae.NetId} — owner mismatch (entity.OwnerID={entity.OwnerID} != applier {ae.AppliedByUserId}).");
                        continue;
                    }

                    // Failure-mode breakdown: capture the *kind* of broken state separately
                    // so verbose logs can distinguish "entity-cleared" (textureID set to 0,
                    // engine-side cause) from "filestorage-missing" (CRC intact on entity but
                    // FileStorage row gone, suggests Remove-without-Store path) — these point
                    // at different triggers. Also keep currentCrc for the verbose log line.
                    uint currentCrc = ReadEntityCrc(entity, ae.ApplyKind, ae.TextureIndex);
                    bool entityCleared = currentCrc == 0;
                    bool fileStorageMissing = false;
                    if (!entityCleared)
                    {
                        try
                        {
                            var existingBytes = FileStorage.server.Get(currentCrc, FileStorage.Type.png, entity.net.ID);
                            fileStorageMissing = existingBytes == null || existingBytes.Length == 0;
                        }
                        catch { fileStorageMissing = true; }
                    }
                    bool broken = entityCleared || fileStorageMissing;

                    if (!broken)
                    {
                        ae.LastVerifiedUtc = nowUtc;
                        // Clear the broken-window tracker since we observed healthy state. A
                        // future failure starts a fresh window from this verified-healthy point.
                        ae.LastObservedBrokenUtc = null;
                        verified++;
                        if (config.SelfHealVerboseLogging)
                            Puts($"[SelfHeal] OK slot={art.Slot} (\"{art.Name}\") entity={ae.NetId} ({ae.ApplyKind}:{entity.ShortPrefabName ?? "?"}) crc={currentCrc} — healthy.");
                        continue;
                    }

                    // First detection of broken state in this failure cycle — stamp the
                    // observed-broken time so subsequent scans can compute a tight bound on
                    // when the trigger fired. Don't overwrite if already set (already tracking
                    // this break since an earlier scan).
                    if (!ae.LastObservedBrokenUtc.HasValue)
                        ae.LastObservedBrokenUtc = nowUtc;

                    string failureMode =
                        entityCleared && fileStorageMissing ? "entity-cleared+filestorage-missing"
                        : entityCleared ? "entity-cleared (textureID=0)"
                        : "filestorage-missing (CRC intact, row gone)";

                    // Bracket the trigger window: art was healthy at LastVerifiedUtc, broken
                    // at LastObservedBrokenUtc. Width depends on scan cadence (connect-only
                    // scans give wide windows; periodic timer narrows it).
                    string triggerWindow;
                    if (ae.LastVerifiedUtc.HasValue)
                    {
                        var window = ae.LastObservedBrokenUtc.Value - ae.LastVerifiedUtc.Value;
                        triggerWindow = $"trigger window: healthy at {ae.LastVerifiedUtc.Value:O}, broken at {ae.LastObservedBrokenUtc.Value:O} (gap={window.TotalMinutes:0.0} min)";
                    }
                    else
                    {
                        triggerWindow = $"trigger window: never verified healthy in this plugin lifetime; broken at {ae.LastObservedBrokenUtc.Value:O}";
                    }

                    // Reset the rolling 24h count if the last repair was long enough ago.
                    if (ae.LastRepairedUtc.HasValue && (nowUtc - ae.LastRepairedUtc.Value).TotalHours >= 24)
                        ae.RepairCount = 0;

                    if (config.SelfHealMaxRepairsPer24h > 0 && ae.RepairCount >= config.SelfHealMaxRepairsPer24h)
                    {
                        PrintWarning($"[SelfHeal] Skip slot={art.Slot} (\"{art.Name}\") entity={ae.NetId} ({ae.ApplyKind}:{entity.ShortPrefabName ?? "?"}) for {applierId}: repair-cap reached ({ae.RepairCount} in last 24h). Failure mode: {failureMode}. {triggerWindow}. Investigate before re-arming.");
                        skipped++;
                        continue;
                    }

                    byte[] cachedBytes = LoadSlotBytes(art);
                    if (cachedBytes == null || cachedBytes.Length == 0)
                    {
                        PrintWarning($"[SelfHeal] Skip slot={art.Slot} entity={ae.NetId}: cached bytes missing/empty at {art.BytesPath}.");
                        skipped++;
                        continue;
                    }

                    // Resize for the target canvas (mirrors ApplyArtToEntity's behavior).
                    byte[] applyBytes = cachedBytes;
                    var targetSize = LookupCanvasSize(entity?.ShortPrefabName);
                    if (targetSize.HasValue)
                    {
                        var (tw, th) = targetSize.Value;
                        if (TryResizePngBytes(cachedBytes, tw, th, out var resized, out _))
                            if (resized != null && resized.Length > 0) applyBytes = resized;
                    }

                    if (ApplyBytesToEntity(entity, ae.ApplyKind, ae.TextureIndex, applyBytes, out var berr))
                    {
                        ae.RepairCount++;
                        ae.LastRepairedUtc = nowUtc;
                        ae.LastVerifiedUtc = nowUtc;
                        // Failure cycle resolved — clear the broken-window tracker so the next
                        // failure starts a fresh window from this just-repaired point.
                        ae.LastObservedBrokenUtc = null;
                        repaired++;
                        string entityDetail = $"{ae.ApplyKind}:{entity.ShortPrefabName ?? "?"}({entity.GetType().Name})";
                        PrintWarning($"[SelfHeal] Repaired slot={art.Slot} (\"{art.Name}\") on {entityDetail} entity={ae.NetId} for {player.displayName} ({applierId}); count={ae.RepairCount}/24h. Failure mode: {failureMode}. {triggerWindow}.");
                    }
                    else
                    {
                        failed++;
                        PrintWarning($"[SelfHeal] Repair FAILED slot={art.Slot} entity={ae.NetId} ({ae.ApplyKind}:{entity.ShortPrefabName ?? "?"}): {berr}. Failure mode: {failureMode}. {triggerWindow}.");
                    }
                }
            }

            if (verified > 0 || repaired > 0 || dropped > 0 || skipped > 0 || failed > 0)
            {
                Puts($"[SelfHeal] {player.displayName} ({applierId}): verified={verified} repaired={repaired} dropped={dropped} skipped={skipped} failed={failed}");
                SaveData();
            }
        }

        // Periodic scan timer (v0.11.2). Off when SelfHealPeriodicScanMinutes <= 0. When on,
        // fires the verify pass for every online player on a recurring interval — much
        // tighter trigger-window brackets than connect-only scans. Diagnostic-only feature;
        // the steady-state cost is one entity lookup + one FileStorage.server.Get per
        // tracked AppliedEntity per online player per interval (sub-ms each).
        private void StartSelfHealPeriodicScanIfEnabled()
        {
            selfHealPeriodicTimer?.Destroy();
            selfHealPeriodicTimer = null;
            if (!config.SelfHealEnabled) return;
            if (config.SelfHealPeriodicScanMinutes <= 0) return;
            float intervalSec = config.SelfHealPeriodicScanMinutes * 60f;
            selfHealPeriodicTimer = timer.Every(intervalSec, () =>
            {
                if (_unloading) return;
                foreach (var p in BasePlayer.activePlayerList)
                {
                    if (p == null || !p.userID.IsSteamId()) continue;
                    try { RunSelfHealForApplier(p); }
                    catch (Exception e) { PrintWarning($"[SelfHeal] Periodic sweep for {p.displayName} threw: {e.Message}"); }
                }
            });
            Puts($"[SelfHeal] Periodic scan enabled — every {config.SelfHealPeriodicScanMinutes} minute(s) for online players.");
        }

        #endregion

        #region Chat command

        [ChatCommand("saveart")]
        private void CmdSaveArt(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!HasUse(player)) { player.ChatMessage(Tag() + Err(L("NoPermission", player))); return; }

            if (args.Length == 0) { OpenBrowsePanel(player, 0UL); return; }

            string sub = args[0].ToLowerInvariant();
            switch (sub)
            {
                case "save":      SubSave(player, args);   break;
                case "apply":     SubApply(player, args, 0UL); break;
                case "list":      SubList(player, args, 0UL);  break;
                case "rename":    SubRename(player, args, 0UL); break;
                case "remove":
                case "delete":    SubRemove(player, args, 0UL); break;
                case "wipe":      SubWipe(player, args);   break;
                case "help":      SubHelp(player);         break;
                case "publish":
                case "unpublish": SubPublish(player, args, 0UL); break;
                case "public":    SubPublic(player, args); break;
                case "share":     SubShare(player, args); break;
                case "unshare":   SubUnshare(player, args); break;
                case "shared":    SubShared(player, args); break;
                case "shared-with-me":
                case "sharedwithme":
                case "shares":    SubSharedWithMe(player); break;
                case "admin":     SubAdmin(player, args);  break;
                case "debug":     SubDebug(player); break;
                default: SubHelp(player); break;
            }
        }

        // /saveart debug — diagnostic. Raycasts forward and prints what's hit, what kind
        // SignArtSaver resolves it as, whether anything is painted, and the per-kind CRC
        // details. Use when /saveart save isn't picking up a sign to diagnose whether the
        // raycast is missing it, the entity class is unrecognized, or the painted-detection
        // is failing. Admin-only: writes to chat AND server log on each call; useful for
        // ops, not for every player. Falls through to the regular help text for non-admins
        // so the command's existence isn't advertised.
        private void SubDebug(BasePlayer player)
        {
            if (!HasAdmin(player)) { SubHelp(player); return; }
            if (player.eyes == null) { player.ChatMessage(Tag() + Err("No eye transform (mid-respawn?).")); return; }
            var ray = player.eyes.HeadRay();
            bool hit = Physics.Raycast(ray, out var info, 16f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (!hit)
            {
                player.ChatMessage(Tag() + Warn("No raycast hit within 16m."));
                return;
            }
            var ent = info.GetEntity() ?? info.collider?.GetComponentInParent<BaseEntity>();
            string entType = ent != null ? ent.GetType().Name : "(null)";
            string entPrefab = ent != null ? (ent.ShortPrefabName ?? "?") : "?";
            string kindResolve = ent != null ? (ResolveKind(ent) ?? "(unrecognized — not Signage/PhotoFrame/CarvablePumpkin/PaintedItemStorageEntity)") : "n/a";
            bool hasArt = ent != null && EntityHasAnyArt(ent);
            // Detail per entity kind.
            string crcDetail = "";
            if (ent is Signage s)
            {
                var crcs = s.GetTextureCRCs();
                crcDetail = crcs == null ? "GetTextureCRCs() = null" : $"GetTextureCRCs() = [{string.Join(", ", crcs)}]";
            }
            else if (ent is PhotoFrame pf)
            {
                var crcs = pf.GetTextureCRCs();
                string c = crcs == null ? "null" : $"[{string.Join(", ", crcs)}]";
                crcDetail = $"GetTextureCRCs()={c}, _overlayTextureCrc={pf._overlayTextureCrc}";
            }
            else if (ent is CarvablePumpkin cp)
            {
                var crcs = cp.textureIDs;
                crcDetail = crcs == null ? "textureIDs = null" : $"textureIDs = [{string.Join(", ", crcs)}]";
            }

            player.ChatMessage(
                Tag() + "<color=#ffff66>Raycast debug</color> (also written to Carbon log)\n" +
                $"  Distance: {info.distance:0.00} m\n" +
                $"  Type:     <color=#7ad>{entType}</color>\n" +
                $"  Prefab:   <color=#7ad>{entPrefab}</color>\n" +
                $"  ResolveKind: <color=#fc5>{kindResolve}</color>\n" +
                $"  EntityHasAnyArt: <color=#fc5>{hasArt}</color>\n" +
                $"  {crcDetail}");

            // Also write to Carbon log so the operator can read it via SSH — chat has
            // no copy/paste in-game. Tagged DEBUG so it's easy to grep.
            Puts($"DEBUG raycast for {player.displayName} ({player.userID}): " +
                 $"distance={info.distance:0.00}m type={entType} prefab={entPrefab} " +
                 $"resolveKind={kindResolve} hasArt={hasArt} {crcDetail}");
        }

        // Toggle a slot's IsPublic flag. Same semantics for "publish" and "unpublish" — both
        // open in either direction; the shared verb keeps muscle memory simple.
        // libUserId=0 means the caller's own library. Non-zero is the admin re-dispatch
        // path (/saveart admin <steamid> unpublish <slot>); SubAdmin gates that on
        // HasAdmin before we get here.
        private void SubPublish(BasePlayer player, string[] args, ulong libUserId)
        {
            if (args.Length < 2 || !int.TryParse(args[1], out var slot))
            {
                player.ChatMessage(Tag() + Err("Usage: /saveart publish <slot>  (toggles public/private)"));
                return;
            }
            ulong target = libUserId == 0 ? (ulong)player.userID : libUserId;
            var lib = store.Players.TryGetValue(target, out var l) ? l : null;
            var art = lib?.Slots.FirstOrDefault(s => s.Slot == slot);
            if (art == null) { player.ChatMessage(Tag() + Err(L("SlotNotFound", player, slot))); return; }
            // Bytes are required to publish — URL-only slots can't be replayed cross-player
            // because the original-owner's signartist.url permission isn't transferable, and
            // re-fetching from the URL on every public apply is bandwidth-wasteful.
            if (string.IsNullOrEmpty(art.BytesPath))
            {
                player.ChatMessage(Tag() + Err(L("UrlOnlyCantPublish", player, slot)));
                return;
            }
            // Per-player public cap (v0.11.5). Counted only on the toggle-TO-public side
            // so admins can always unpublish a slot regardless of the current cap value.
            // Admins bypass the cap entirely — operator may need to publish a moderation
            // exemplar without being capped by the same rule players hit.
            bool togglingToPublic = !art.IsPublic;
            if (togglingToPublic && config.MaxPublicSlotsPerPlayer > 0 && !HasAdmin(player))
            {
                int currentPublic = lib.Slots.Count(s => s != null && s.IsPublic);
                if (currentPublic >= config.MaxPublicSlotsPerPlayer)
                {
                    player.ChatMessage(Tag() + Err(L("PublishCapHit", player, currentPublic, config.MaxPublicSlotsPerPlayer)));
                    return;
                }
            }
            art.IsPublic = !art.IsPublic;
            art.PublishedUtc = art.IsPublic ? DateTime.UtcNow : (DateTime?)null;
            StampLib(lib);
            string verb = art.IsPublic ? "published" : "unpublished";
            string color = art.IsPublic ? "#55ff55" : "#ffcc55";
            string ownerNote = libUserId != 0 ? $" [{ResolveDisplayName(libUserId)}'s lib]" : "";
            player.ChatMessage(Tag() + $"<color={color}>{L("SlotPublishedTpl", player, slot, EscapeRich(art.Name), verb, ownerNote)}</color>");
            // If panel is open, refresh so the row reflects the new state.
            if (openPanels.ContainsKey(player.userID)) RefreshBrowsePanel(player);
        }

        // /saveart share <slot> <name|steamid> — grant a buyer copy access on this slot.
        // Multi-buyer: same slot can be shared with many people. Idempotent on re-share
        // (updates the cached display name but doesn't bump SharedUtc — the original grant
        // time is the meaningful one for the artist's UI ordering).
        private void SubShare(BasePlayer player, string[] args)
        {
            if (args.Length < 3)
            {
                player.ChatMessage(Tag() + Err("Usage: /saveart share <slot> <name|steamid>"));
                return;
            }
            if (!int.TryParse(args[1], out var slot) || slot < 1)
            {
                player.ChatMessage(Tag() + Err("Slot must be a positive integer.")); return;
            }
            var lib = store.Players.TryGetValue((ulong)player.userID, out var l) ? l : null;
            var art = lib?.Slots.FirstOrDefault(s => s.Slot == slot);
            if (art == null) { player.ChatMessage(Tag() + Err($"No such slot ({slot}) in your library.")); return; }
            if (string.IsNullOrEmpty(art.BytesPath))
            {
                player.ChatMessage(Tag() + Err(L("UrlOnlyCantShare", player, slot)));
                return;
            }

            // Everything after args[1] is the name (so multi-word names work without quoting).
            string nameArg = string.Join(" ", args.Skip(2)).Trim();
            if (!TryResolvePlayer(nameArg, player, out var buyerId, out var buyerName, out var err))
            {
                player.ChatMessage(Tag() + Err(err));
                return;
            }
            if (buyerId == (ulong)player.userID)
            {
                player.ChatMessage(Tag() + Err(L("ShareSelfDenied", player))); return;
            }
            if (art.SharedWith == null) art.SharedWith = new List<ShareEntry>();
            var existing = art.SharedWith.FirstOrDefault(s => s.SteamId == buyerId);
            string buyerEsc = EscapeRich(buyerName);
            if (existing != null)
            {
                existing.NameAtShare = buyerName;
                player.ChatMessage(Tag() + Warn(L("ShareAlready", player, buyerEsc, existing.SharedUtc)));
                return;
            }
            art.SharedWith.Add(new ShareEntry { SteamId = buyerId, NameAtShare = buyerName, SharedUtc = DateTime.UtcNow });
            StampLib(lib);
            StampRoster(buyerId, buyerName);
            player.ChatMessage(Tag() + L("ShareDone", player, slot, EscapeRich(art.Name), buyerEsc, art.SharedWith.Count));
            NotifyShareAdded(buyerId, (ulong)player.userID, art);
            if (openPanels.ContainsKey(player.userID)) RefreshBrowsePanel(player);
        }

        // /saveart unshare <slot> <name|steamid> — revoke a buyer's access. Silently
        // succeeds if the buyer wasn't on the list (idempotent).
        private void SubUnshare(BasePlayer player, string[] args)
        {
            if (args.Length < 3)
            {
                player.ChatMessage(Tag() + Err("Usage: /saveart unshare <slot> <name|steamid>"));
                return;
            }
            if (!int.TryParse(args[1], out var slot) || slot < 1)
            {
                player.ChatMessage(Tag() + Err("Slot must be a positive integer.")); return;
            }
            var lib = store.Players.TryGetValue((ulong)player.userID, out var l) ? l : null;
            var art = lib?.Slots.FirstOrDefault(s => s.Slot == slot);
            if (art == null) { player.ChatMessage(Tag() + Err($"No such slot ({slot}) in your library.")); return; }
            if (art.SharedWith == null || art.SharedWith.Count == 0)
            {
                player.ChatMessage(Tag() + Warn(L("UnshareEmpty", player, slot))); return;
            }

            string nameArg = string.Join(" ", args.Skip(2)).Trim();
            if (!TryResolvePlayer(nameArg, player, out var buyerId, out var buyerName, out var err))
            {
                player.ChatMessage(Tag() + Err(err));
                return;
            }
            int removed = art.SharedWith.RemoveAll(s => s.SteamId == buyerId);
            string buyerEsc = EscapeRich(buyerName);
            if (removed == 0)
            {
                player.ChatMessage(Tag() + Warn(L("UnshareNotOnList", player, buyerEsc, slot)));
                return;
            }
            StampLib(lib);
            player.ChatMessage(Tag() + L("UnshareDone", player, buyerEsc, slot, art.SharedWith.Count));
            if (openPanels.ContainsKey(player.userID)) RefreshBrowsePanel(player);
        }

        // /saveart shared <slot> — list the buyers on one of the artist's slots.
        // Pass no slot to list ALL slots that have at least one buyer (artist's overview).
        private void SubShared(BasePlayer player, string[] args)
        {
            var lib = store.Players.TryGetValue((ulong)player.userID, out var l) ? l : null;
            if (lib == null || lib.Slots == null || lib.Slots.Count == 0)
            {
                player.ChatMessage(Tag() + Warn("Your library is empty.")); return;
            }
            if (args.Length >= 2 && int.TryParse(args[1], out var slot))
            {
                var art = lib.Slots.FirstOrDefault(s => s.Slot == slot);
                if (art == null) { player.ChatMessage(Tag() + Err(L("SlotNotFound", player, slot))); return; }
                if (art.SharedWith == null || art.SharedWith.Count == 0)
                {
                    player.ChatMessage(Tag() + $"Slot {slot} (\"{art.Name}\") has no buyers."); return;
                }
                var sb = new StringBuilder();
                sb.AppendLine(Tag() + $"<color=#ffff66>Slot {slot}</color> (\"{art.Name}\") shared with {art.SharedWith.Count}:");
                foreach (var s in art.SharedWith.OrderBy(x => x.SharedUtc))
                {
                    string display = EscapeRich(ResolveDisplayName(s.SteamId));
                    sb.AppendLine($"  <color=#7ad>{display}</color> — {s.SharedUtc:yyyy-MM-dd}");
                }
                player.ChatMessage(sb.ToString());
                return;
            }
            // No slot arg → overview of all shared slots.
            var rows = lib.Slots.Where(s => s.SharedWith != null && s.SharedWith.Count > 0).OrderBy(s => s.Slot).ToList();
            if (rows.Count == 0)
            {
                player.ChatMessage(Tag() + "No slots are currently shared with anyone."); return;
            }
            var sb2 = new StringBuilder();
            sb2.AppendLine(Tag() + $"<color=#ffff66>Shared slots ({rows.Count}):</color>");
            foreach (var a in rows)
            {
                sb2.AppendLine($"  Slot <color=#ffff66>{a.Slot}</color> (\"{a.Name}\") → <color=#7ad>{a.SharedWith.Count}</color> buyer(s). Use /saveart shared {a.Slot} for names.");
            }
            player.ChatMessage(sb2.ToString());
        }

        // /saveart shared-with-me — list slots that other artists have shared with the caller.
        private void SubSharedWithMe(BasePlayer player)
        {
            ulong me = (ulong)player.userID;
            var rows = new List<(SavedArt art, ulong artistId)>();
            foreach (var kv in store.Players)
            {
                var lib2 = kv.Value;
                if (lib2?.Slots == null) continue;
                foreach (var a in lib2.Slots)
                {
                    if (a.SharedWith == null) continue;
                    if (a.SharedWith.Any(s => s.SteamId == me)) rows.Add((a, kv.Key));
                }
            }
            if (rows.Count == 0)
            {
                player.ChatMessage(Tag() + "Nobody has shared art with you yet."); return;
            }
            // Newest first so fresh commissions show at top.
            rows.Sort((x, y) =>
            {
                var xt = x.art.SharedWith.First(s => s.SteamId == me).SharedUtc;
                var yt = y.art.SharedWith.First(s => s.SteamId == me).SharedUtc;
                return yt.CompareTo(xt);
            });
            var sb = new StringBuilder();
            sb.AppendLine(Tag() + $"<color=#ffff66>Shared with you ({rows.Count}):</color>");
            foreach (var r in rows)
            {
                string artist = EscapeRich(ResolveDisplayName(r.artistId));
                sb.AppendLine($"  <color=#7ad>{artist}</color> — slot <color=#ffff66>{r.art.Slot}</color> \"{EscapeRich(r.art.Name)}\" — <color=#aaaaaa>open /saveart → Shared tab to apply</color>");
            }
            player.ChatMessage(sb.ToString());
        }

        // Open the Public Gallery tab. Optional rest-of-args become the search query.
        private void SubPublic(BasePlayer player, string[] args)
        {
            string query = args.Length >= 2 ? string.Join(" ", args.Skip(1)).Trim() : null;
            if (!openPanels.TryGetValue(player.userID, out var panel))
            {
                openPanels[player.userID] = new BrowsePanel { Tab = TabPublic, Page = 1, PublicSearchQuery = query };
            }
            else
            {
                panel.Tab = TabPublic;
                panel.Page = 1;
                panel.PublicSearchQuery = query;
            }
            RefreshBrowsePanel(player);
        }

        // Byte-mode capture: read the PNG bytes already on the painted sign and stash them
        // locally. Works regardless of how the sign was painted (vanilla painter, Sign Artist,
        // CopyPaste paste, etc.) — anything that ended up in FileStorage is fair game.
        private void SubSave(BasePlayer player, string[] args)
        {
            if (!TryRaycastSign(player, out var entity, out var kind, out var texIdx))
            {
                player.ChatMessage(Tag() + Err(L("LookAtSign", player)));
                return;
            }
            if (config.RequireOwnerSave && entity.OwnerID != player.userID && !HasAdmin(player))
            {
                player.ChatMessage(Tag() + Err(L("NotOwner", player)));
                return;
            }

            byte[] bytes = FetchPngFromEntity(entity, texIdx);
            if (bytes == null || bytes.Length == 0)
            {
                player.ChatMessage(Tag() + Err(L("NoImageOnSign", player)));
                return;
            }

            string name = args.Length >= 2 ? string.Join(" ", args.Skip(1)).Trim() : null;

            CaptureToLibrary(player, (ulong)player.userID, url: null, raw: false, kind: kind, texIdx: texIdx, bytes: bytes, entity: entity, explicitName: name, autoCaptured: false);
        }

        private void SubApply(BasePlayer player, string[] args, ulong libUserId)
        {
            if (args.Length < 2 || !int.TryParse(args[1], out var slot))
            {
                player.ChatMessage(Tag() + Err("Usage: /saveart apply <slot>"));
                return;
            }
            ulong target = libUserId == 0 ? (ulong)player.userID : libUserId;
            var lib = store.Players.TryGetValue(target, out var l) ? l : null;
            var art = lib?.Slots.FirstOrDefault(s => s.Slot == slot);
            if (art == null) { player.ChatMessage(Tag() + Err(L("SlotNotFound", player, slot))); return; }

            // Prefer immediate apply if the player is already aiming at a sign.
            if (TryRaycastSign(player, out var entity, out var kind, out _))
            {
                ApplyArtToEntity(player, entity, kind, art, target);
                return;
            }

            awaitingApply[player.userID] = new PendingApply
            {
                Slot = slot,
                ArmedUtc = DateTime.UtcNow,
                TargetUserId = libUserId,
            };
            player.ChatMessage(Tag() + Ok(L("SlotArmed", player, slot, config.PendingApplyTimeoutSeconds)));
        }

        private void SubList(BasePlayer player, string[] args, ulong libUserId)
        {
            ulong target = libUserId == 0 ? (ulong)player.userID : libUserId;
            var lib = store.Players.TryGetValue(target, out var l) ? l : null;
            int count = lib?.Slots?.Count ?? 0;
            if (count == 0) { player.ChatMessage(Tag() + "Library is empty. Paint a sign with /sil <url> and the URL will be auto-saved."); return; }

            int page = 1;
            if (args.Length >= 2 && int.TryParse(args[1], out var p) && p >= 1) page = p;
            int pageSize = config.RowsPerPage;
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)count / pageSize));
            if (page > totalPages) page = totalPages;

            var sb = new StringBuilder();
            sb.AppendLine(Tag() + $"Library — {count}/{config.SlotsPerPlayer} slots, page {page}/{totalPages}");
            foreach (var art in lib.Slots.OrderBy(s => s.Slot).Skip((page - 1) * pageSize).Take(pageSize))
            {
                sb.AppendLine($"  [{art.Slot}] <color=#ffff66>{art.Name}</color> · {art.EntityKind}/tex{art.TextureIndex} · {FormatAge(art.SavedUtc)} · {Truncate(art.Url, config.UrlDisplayLen)}");
            }
            player.ChatMessage(sb.ToString().TrimEnd());
        }

        private void SubRename(BasePlayer player, string[] args, ulong libUserId)
        {
            if (args.Length < 3 || !int.TryParse(args[1], out var slot))
            {
                player.ChatMessage(Tag() + Err("Usage: /saveart rename <slot> <new name>"));
                return;
            }
            string newName = SanitizeSlotName(string.Join(" ", args.Skip(2)));
            if (string.IsNullOrEmpty(newName) || newName.Length > 32)
            {
                player.ChatMessage(Tag() + Err(L("NamePolicy", player)));
                return;
            }
            ulong target = libUserId == 0 ? (ulong)player.userID : libUserId;
            var lib = store.Players.TryGetValue(target, out var l) ? l : null;
            var art = lib?.Slots.FirstOrDefault(s => s.Slot == slot);
            if (art == null) { player.ChatMessage(Tag() + Err(L("SlotNotFound", player, slot))); return; }
            art.Name = newName;
            StampLib(lib);
            player.ChatMessage(Tag() + Ok(L("SlotRenamed", player, slot, newName)));
        }

        private void SubRemove(BasePlayer player, string[] args, ulong libUserId)
        {
            if (args.Length < 2 || !int.TryParse(args[1], out var slot))
            {
                player.ChatMessage(Tag() + Err("Usage: /saveart remove <slot>"));
                return;
            }
            ulong target = libUserId == 0 ? (ulong)player.userID : libUserId;
            var lib = store.Players.TryGetValue(target, out var l) ? l : null;
            int idx = lib?.Slots.FindIndex(s => s.Slot == slot) ?? -1;
            if (idx < 0) { player.ChatMessage(Tag() + Err(L("SlotNotFound", player, slot))); return; }

            // Two-step confirm: first call arms; second call within ConfirmDeleteSeconds confirms.
            if (openPanels.TryGetValue(player.userID, out var panel) &&
                panel.PendingDeleteSlot == slot &&
                panel.PendingDeleteArmedUtc.HasValue &&
                (DateTime.UtcNow - panel.PendingDeleteArmedUtc.Value).TotalSeconds < config.ConfirmDeleteSeconds)
            {
                DeleteSlotPng(lib.Slots[idx]);
                lib.Slots.RemoveAt(idx);
                StampLib(lib);
                panel.PendingDeleteSlot = null;
                panel.PendingDeleteArmedUtc = null;
                player.ChatMessage(Tag() + Ok(L("SlotRemoved", player, slot)));
                if (openPanels.ContainsKey(player.userID)) RefreshBrowsePanel(player);
                return;
            }

            if (!openPanels.ContainsKey(player.userID))
                openPanels[player.userID] = new BrowsePanel { AdminTargetId = libUserId };
            var p2 = openPanels[player.userID];
            p2.PendingDeleteSlot = slot;
            p2.PendingDeleteArmedUtc = DateTime.UtcNow;
            player.ChatMessage(Tag() + Warn(L("ConfirmDelete", player, slot, config.ConfirmDeleteSeconds)));
        }

        private void SubWipe(BasePlayer player, string[] args)
        {
            if (args.Length >= 2 && args[1].ToLowerInvariant() == "confirm")
            {
                if (wipeArmedUtc.TryGetValue(player.userID, out var armedUtc) &&
                    (DateTime.UtcNow - armedUtc).TotalSeconds < config.WipeConfirmSeconds)
                {
                    if (store.Players.TryGetValue(player.userID, out var lib) && lib.Slots != null)
                    {
                        int n = lib.Slots.Count;
                        // Delete every PNG on disk before clearing the in-memory list so we
                        // don't orphan files. DeleteUserImagesDir is the broadside — clears
                        // anything stale beyond what's in the slot list (defensive).
                        foreach (var s in lib.Slots) DeleteSlotPng(s);
                        DeleteUserImagesDir((ulong)player.userID);
                        lib.Slots.Clear();
                        StampLib(lib);
                        wipeArmedUtc.Remove(player.userID);
                        player.ChatMessage(Tag() + Ok(L("WipeConfirmDone", player, n)));
                        return;
                    }
                    player.ChatMessage(Tag() + L("WipeEmpty", player));
                    return;
                }
                player.ChatMessage(Tag() + Err(L("WipeConfirmExpired", player)));
                return;
            }
            wipeArmedUtc[player.userID] = DateTime.UtcNow;
            player.ChatMessage(Tag() + Warn(L("WipeConfirmPrompt", player, config.WipeConfirmSeconds)));
        }

        private void SubHelp(BasePlayer player)
        {
            string publicCapNote = config.MaxPublicSlotsPerPlayer > 0
                ? $" (cap: {config.MaxPublicSlotsPerPlayer} public slots / player)"
                : " (no cap)";
            string adminBlock = HasAdmin(player)
                ? "\n<color=#ffcc55>Admin:</color>\n" +
                  "  /saveart admin <steamid> <list|apply|rename|remove|publish|unpublish> [args]\n" +
                  "  /saveart debug                    — raycast diagnostic (admin-only)"
                : "";
            player.ChatMessage(
                Tag() + "<color=#ffff66>Sign Art Library commands:</color>\n" +
                "  /saveart                          — open browse panel (My Library tab)\n" +
                "  /saveart save [name]              — save the painted sign you're aiming at (byte-mode)\n" +
                "  /saveart apply <slot>             — repaint the sign you're aiming at\n" +
                "  /saveart rename <slot> <name>     — rename a slot\n" +
                "  /saveart remove <slot>            — delete a slot (two-step)\n" +
                "  /saveart wipe                     — wipe whole library (two-step)\n" +
                $"  /saveart publish <slot>           — toggle a slot public/private{publicCapNote}\n" +
                "  /saveart public [search]          — open Public Gallery (optional search by artist/name)\n" +
                "  /saveart share <slot> <name>      — grant a specific buyer copy access (commission/sell)\n" +
                "  /saveart unshare <slot> <name>    — revoke a buyer's access\n" +
                "  /saveart shared [slot]            — list buyers on a slot, or all your shared slots\n" +
                "  /saveart shared-with-me           — list slots other artists shared with you\n" +
                "  /saveart list [page]              — chat list of own slots\n" +
                "  Click <color=#7ad>View</color> on any row in the panel to preview the image before applying." +
                adminBlock + "\n" +
                $"<color=#aaaaaa>Capture: /sil [url] via Sign Artist auto-captures URL + bytes; /saveart save reads bytes off any painted sign you own (vanilla painter or Sign Artist). Apply prefers bytes (no network hit) and auto-resizes to the target canvas via System.Drawing. Saved bytes live on the SERVER under SignArtSaver/images/[steamid]/[slot].png and survive wipes. Auto-capture is {(config.AutoCapture ? "ON" : "OFF")}.</color>"
            );
        }

        private void SubAdmin(BasePlayer player, string[] args)
        {
            if (!HasAdmin(player)) { player.ChatMessage(Tag() + Err(L("AdminOnly", player))); return; }
            if (args.Length < 3) { player.ChatMessage(Tag() + Err("Usage: /saveart admin <steamid> <list|apply|remove|rename|publish|unpublish> [args]")); return; }
            if (!ulong.TryParse(args[1], out var target) || !target.IsSteamId())
            {
                player.ChatMessage(Tag() + Err(L("BadSteamId", player)));
                return;
            }
            // Re-dispatch to the matching subcommand with libUserId=target. Publish and
            // unpublish (v0.11.5) — moderation for the public gallery. Admins can flip
            // either direction; the chat path bypasses the per-player public cap (admins
            // need to be able to unpublish even when the operator has lowered the cap).
            var sub = args[2].ToLowerInvariant();
            var rest = args.Skip(2).ToArray();
            switch (sub)
            {
                case "apply":     SubApply(player, rest, target);   break;
                case "list":      SubList(player, rest, target);    break;
                case "rename":    SubRename(player, rest, target);  break;
                case "remove":
                case "delete":    SubRemove(player, rest, target);  break;
                case "publish":
                case "unpublish": SubPublish(player, rest, target); break;
                default: player.ChatMessage(Tag() + Err("Subcommand: list | apply | rename | remove | publish | unpublish")); break;
            }
        }

        #endregion

        #region Console commands (CUI dispatch)

        [ConsoleCommand("signartsaver.ui.close")]
        private void CcUiClose(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            if (openPanels.TryGetValue(player.userID, out var panel))
                ClosePreview(player, panel, refreshPanel: false);
            DestroyAllUi(player);
            openPanels.Remove(player.userID);
        }

        [ConsoleCommand("signartsaver.ui.page")]
        private void CcUiPage(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;
            string dir = arg.GetString(0) ?? "";
            int delta = dir == "next" ? 1 : (dir == "prev" ? -1 : 0);
            if (delta == 0) return;
            panel.Page = Math.Max(1, panel.Page + delta);
            RefreshBrowsePanel(player);
        }

        [ConsoleCommand("signartsaver.ui.apply")]
        private void CcUiApply(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            int slot = arg.GetInt(0, -1);
            if (slot < 1) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;

            ulong target = panel.AdminTargetId != 0 ? panel.AdminTargetId : (ulong)player.userID;
            var lib = store.Players.TryGetValue(target, out var l) ? l : null;
            var art = lib?.Slots.FirstOrDefault(s => s.Slot == slot);
            if (art == null) { player.ChatMessage(Tag() + Err($"Slot {slot} no longer exists.")); return; }

            // Close panel so the player can aim at the sign without the CUI in the way.
            DestroyAllUi(player);
            openPanels.Remove(player.userID);

            if (TryRaycastSign(player, out var entity, out var kind, out _))
            {
                ApplyArtToEntity(player, entity, kind, art, target);
                return;
            }

            awaitingApply[player.userID] = new PendingApply
            {
                Slot = slot,
                ArmedUtc = DateTime.UtcNow,
                TargetUserId = panel.AdminTargetId,
            };
            player.ChatMessage(Tag() + Ok(L("SlotArmed", player, slot, config.PendingApplyTimeoutSeconds)));
        }

        [ConsoleCommand("signartsaver.ui.delete")]
        private void CcUiDelete(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            int slot = arg.GetInt(0, -1);
            if (slot < 1) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;
            panel.PendingDeleteSlot = slot;
            panel.PendingDeleteArmedUtc = DateTime.UtcNow;
            RefreshBrowsePanel(player);
        }

        // Toolbar: save the sign the player is currently aiming at — same one-shot pattern
        // as Apply. Cursor mode in CUI doesn't move the player's head ray; whatever they
        // were looking at when they opened /saveart is still in their crosshair, so the
        // raycast fires immediately. Only fall back to the USE-key arm if nothing's there.
        [ConsoleCommand("signartsaver.ui.save")]
        private void CcUiSave(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;

            // Cancel any prior apply arming so the two flows can't tangle.
            awaitingApply.Remove(player.userID);

            // Tear down panel + preview so the cursor releases and the head ray is unblocked.
            ClosePreview(player, panel, refreshPanel: false);
            DestroyAllUi(player);
            openPanels.Remove(player.userID);

            if (TryRaycastSign(player, out var entity, out var kind, out var texIdx))
            {
                if (config.RequireOwnerSave && entity.OwnerID != player.userID && !HasAdmin(player))
                {
                    player.ChatMessage(Tag() + Err(L("NotOwner", player)));
                    return;
                }
                byte[] bytes = FetchPngFromEntity(entity, texIdx);
                if (bytes == null || bytes.Length == 0)
                {
                    player.ChatMessage(Tag() + Err(L("NoImageOnSign", player)));
                    return;
                }
                CaptureToLibrary(player, (ulong)player.userID, url: null, raw: false, kind: kind, texIdx: texIdx, bytes: bytes, entity: entity, explicitName: null, autoCaptured: false);
                return;
            }

            // No target — arm the USE-key fallback so the player can aim and press USE.
            awaitingSave[player.userID] = new PendingSave { ArmedUtc = DateTime.UtcNow };
            player.ChatMessage(Tag() + Warn($"No sign in your aim. Look at one and press USE within {config.PendingApplyTimeoutSeconds:0}s, or open /saveart again while aimed."));
        }

        // Toolbar: open the help modal. Read-only; closes via its own X or by reopening
        // the main panel (clicking the close button on the modal).
        [ConsoleCommand("signartsaver.ui.help")]
        private void CcUiHelp(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            BuildHelpModal(player);
        }

        [ConsoleCommand("signartsaver.ui.help.close")]
        private void CcUiHelpClose(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiHelpModal);
        }

        // Toolbar: Import URL — capture the entity in the player's aim NOW (cursor mode
        // doesn't move the head ray), open a modal with two URL input fields. The fields
        // dispatch to apply-only / apply-and-save respectively when the player presses
        // Enter. The captured entity reference is used directly so we don't re-raycast
        // after the cursor has moved into the input field.
        [ConsoleCommand("signartsaver.ui.import.start")]
        private void CcUiImportStart(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;

            if (!TryRaycastSign(player, out var entity, out var kind, out var texIdx))
            {
                player.ChatMessage(Tag() + Err("Look at a sign, photo frame, or pumpkin first, then click Import URL."));
                return;
            }
            if (config.RequireOwnerSave && entity.OwnerID != player.userID && !HasAdmin(player))
            {
                player.ChatMessage(Tag() + Err(L("NotOwner", player)));
                return;
            }

            awaitingImport[player.userID] = new PendingImport
            {
                Entity = entity,
                Kind = kind,
                TextureIndex = texIdx,
                ArmedUtc = DateTime.UtcNow,
            };

            // Tear down the main panel so the import modal is on a clean screen — same
            // pattern as the preview modal. RefreshBrowsePanel rebuilds on close.
            CuiHelper.DestroyUi(player, UiPanel);
            CuiHelper.DestroyUi(player, UiBackdrop);

            BuildImportUrlModal(player);
        }

        [ConsoleCommand("signartsaver.ui.import.close")]
        private void CcUiImportClose(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiImportModal);
            awaitingImport.Remove(player.userID);
            // Restore the main panel.
            if (openPanels.ContainsKey(player.userID)) RefreshBrowsePanel(player);
        }

        // URL input → apply (no slot taken). Always sets skipNextCapture so OnImagePost's
        // auto-capture path bails on the resulting event. If the player later wants to save
        // the painted result, they can aim at it and click Save Sign in the toolbar — that
        // path uses the same bytes the sign now displays. v0.8.3 dropped the duplicate
        // "Apply & Save" mode since it just shadowed Save Sign's existing flow.
        [ConsoleCommand("signartsaver.ui.import.apply")]
        private void CcUiImportApply(ConsoleSystem.Arg arg) => DispatchImport(arg);

        private void DispatchImport(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (!awaitingImport.TryGetValue(player.userID, out var pending))
            {
                player.ChatMessage(Tag() + Err("Import session expired — re-open the panel and click Import URL."));
                return;
            }
            // CuiInputField appends typed text after Command; arg.Args is the typed URL.
            string url = arg.Args != null ? string.Join(" ", arg.Args).Trim() : "";
            // CUI input fields don't auto-clear placeholders on focus, so the user might
            // have pressed Enter without changing the field. Detect the placeholder we set
            // and treat it as empty.
            if (url.Equals(ImportPlaceholder, StringComparison.OrdinalIgnoreCase))
            {
                player.ChatMessage(Tag() + Err("Click into the field, paste a URL, then press Enter."));
                return;
            }
            // Sometimes player types alongside the placeholder text; strip a leading copy
            // if present.
            if (url.StartsWith(ImportPlaceholder, StringComparison.OrdinalIgnoreCase))
                url = url.Substring(ImportPlaceholder.Length).TrimStart();
            if (string.IsNullOrEmpty(url))
            {
                player.ChatMessage(Tag() + Err("URL was empty."));
                return;
            }
            if (UrlIsBlocked(url))
            {
                player.ChatMessage(Tag() + Err("URL contains a blocked substring (auth token?)."));
                return;
            }
            // Strip enclosing quotes if the player wrapped the URL.
            if (url.Length >= 2 && url[0] == '"' && url[url.Length - 1] == '"') url = url.Substring(1, url.Length - 2);

            if (pending.Entity == null || pending.Entity.IsDestroyed)
            {
                player.ChatMessage(Tag() + Err("Target sign is no longer there."));
                awaitingImport.Remove(player.userID);
                return;
            }
            if (SignArtist == null)
            {
                player.ChatMessage(Tag() + Err("Sign Artist plugin offline."));
                return;
            }

            // Set the bypass flag BEFORE calling the API so the resulting OnImagePost
            // skips auto-capture (Import URL never takes a slot — Save Sign in the toolbar
            // is the canonical "I want to save this" path).
            skipNextCapture.Add((ulong)player.userID);

            try
            {
                if (pending.Kind == KindSign && pending.Entity is Signage sign)
                    SignArtist.Call("API_SkinSign", player, sign, url, false, pending.TextureIndex);
                else if (pending.Kind == KindPhotoFrame && pending.Entity is PhotoFrame frame)
                    SignArtist.Call("API_SkinPhotoFrame", player, frame, url, false);
                else if (pending.Kind == KindPumpkin && pending.Entity is CarvablePumpkin pumpkin)
                    SignArtist.Call("API_SkinPumpkin", player, pumpkin, url, false);
                else if (pending.Kind == KindPaintedItem)
                {
                    // Sign Artist v1.4.6 has no API_Skin* for PaintedItemStorageEntity. URL
                    // import via Sign Artist is not possible for these entities; the user
                    // must paint via the vanilla in-game UI first and then /saveart save.
                    skipNextCapture.Remove((ulong)player.userID);
                    player.ChatMessage(Tag() + Err("Sign Artist doesn't support drawable windows / paintable targets — paint with the vanilla UI, then Save Sign."));
                    return;
                }
                else
                {
                    skipNextCapture.Remove((ulong)player.userID);
                    player.ChatMessage(Tag() + Err("Internal: kind/entity mismatch."));
                    return;
                }
            }
            catch (Exception e)
            {
                skipNextCapture.Remove((ulong)player.userID);
                PrintWarning($"Import URL via Sign Artist threw: {e.Message}");
                player.ChatMessage(Tag() + Err("Apply failed — Sign Artist threw. Check server log."));
                return;
            }

            awaitingImport.Remove(player.userID);
            CuiHelper.DestroyUi(player, UiImportModal);

            player.ChatMessage(Tag() + Ok("Applied. To save it, aim at the sign and click Save Sign in the toolbar."));

            // Bring the main panel back so the user can keep working.
            if (openPanels.ContainsKey(player.userID)) RefreshBrowsePanel(player);
        }

        // Toolbar: arm a wipe (sets wipeArmedUtc; the chat /saveart wipe path uses the same
        // dict, so a player can mix-and-match arming via chat and confirming via panel).
        [ConsoleCommand("signartsaver.ui.wipe")]
        private void CcUiWipe(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            wipeArmedUtc[player.userID] = DateTime.UtcNow;
            player.ChatMessage(Tag() + Warn($"Click the red CONFIRM WIPE button within {config.WipeConfirmSeconds:0}s to delete ALL your saved art. This is irreversible."));
            if (openPanels.ContainsKey(player.userID)) RefreshBrowsePanel(player);
        }

        [ConsoleCommand("signartsaver.ui.wipe.confirm")]
        private void CcUiWipeConfirm(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            // Reuse the chat-path subcommand so validation + window check + cleanup logic
            // stays in one place.
            SubWipe(player, new[] { "wipe", "confirm" });
            if (openPanels.ContainsKey(player.userID)) RefreshBrowsePanel(player);
        }

        // Cross-player apply from the Public Gallery tab. Args[0] = owner steamid,
        // Args[1] = slot. The slot must be marked IsPublic on the owner's library.
        [ConsoleCommand("signartsaver.ui.apply.public")]
        private void CcUiApplyPublic(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            if (!ulong.TryParse(arg.Args[0], out var ownerId) || !ownerId.IsSteamId()) return;
            if (!int.TryParse(arg.Args[1], out var slot) || slot < 1) return;

            if (!store.Players.TryGetValue(ownerId, out var lib))
            {
                player.ChatMessage(Tag() + Err("Owner library not found."));
                return;
            }
            var art = lib.Slots.FirstOrDefault(s => s.Slot == slot && s.IsPublic);
            if (art == null)
            {
                player.ChatMessage(Tag() + Err($"Public slot {slot} not found (or unpublished)."));
                return;
            }
            ApplyArtCrossPlayerArm(player, art, ownerId, slot);
        }

        // Apply a slot that the artist has explicitly shared with the caller. Same arming
        // pattern as the public-apply path but the access gate is the share allowlist (or
        // admin override). Wired from the "Shared with me" tab's Apply button.
        [ConsoleCommand("signartsaver.ui.apply.shared")]
        private void CcUiApplyShared(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            if (!ulong.TryParse(arg.Args[0], out var ownerId) || !ownerId.IsSteamId()) return;
            if (!int.TryParse(arg.Args[1], out var slot) || slot < 1) return;

            if (!store.Players.TryGetValue(ownerId, out var lib))
            {
                player.ChatMessage(Tag() + Err("Artist library not found (slot may have been deleted)."));
                return;
            }
            var art = lib.Slots.FirstOrDefault(s => s.Slot == slot);
            if (art == null)
            {
                player.ChatMessage(Tag() + Err($"Slot {slot} no longer exists in {ResolveDisplayName(ownerId)}'s library."));
                return;
            }
            if (!CanAccessSlot(art, ownerId, (ulong)player.userID, player))
            {
                player.ChatMessage(Tag() + Err("Access revoked — that slot is no longer shared with you."));
                return;
            }
            ApplyArtCrossPlayerArm(player, art, ownerId, slot);
        }

        // Shared "close panel + (apply now if aimed at sign, else arm USE)" path used by
        // both the public-apply and shared-apply console commands.
        private void ApplyArtCrossPlayerArm(BasePlayer player, SavedArt art, ulong ownerId, int slot)
        {

            DestroyAllUi(player);
            openPanels.Remove(player.userID);

            if (TryRaycastSign(player, out var entity, out var kind, out _))
            {
                ApplyArtToEntity(player, entity, kind, art, ownerId);
                return;
            }

            // Arm with TargetUserId = the artist's id so OnPlayerInput's lookup uses the
            // right library when the player aims and presses USE.
            awaitingApply[player.userID] = new PendingApply
            {
                Slot = slot,
                ArmedUtc = DateTime.UtcNow,
                TargetUserId = ownerId,
            };
            player.ChatMessage(Tag() + Ok(L("SlotArmedNamed", player, slot, EscapeRich(art.Name), config.PendingApplyTimeoutSeconds)));
        }

        // Open the preview modal for a slot. Args: <ownerId> <slot>.
        // ownerId=0 is shorthand for "the panel's current owner" (self or admin target).
        [ConsoleCommand("signartsaver.ui.preview")]
        private void CcUiPreview(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            if (!ulong.TryParse(arg.Args[0], out var ownerArg)) return;
            if (!int.TryParse(arg.Args[1], out var slot) || slot < 1) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;

            ulong ownerId = ownerArg != 0 ? ownerArg : (panel.AdminTargetId != 0 ? panel.AdminTargetId : (ulong)player.userID);

            if (!store.Players.TryGetValue(ownerId, out var lib))
            {
                player.ChatMessage(Tag() + Err("Owner library not found."));
                return;
            }
            var art = lib.Slots.FirstOrDefault(s => s.Slot == slot);
            if (art == null) { player.ChatMessage(Tag() + Err($"Slot {slot} not found.")); return; }
            // Cross-player preview requires public, an explicit share, or admin (v0.10.1).
            if (!CanAccessSlot(art, ownerId, (ulong)player.userID, player))
            {
                player.ChatMessage(Tag() + Err("That slot is private."));
                return;
            }
            if (string.IsNullOrEmpty(art.BytesPath))
            {
                player.ChatMessage(Tag() + Err("URL-only slots can't be previewed (no local bytes)."));
                return;
            }

            // Read PNG bytes from disk and push them into FileStorage with a sentinel
            // NetworkableId so the client can fetch by CRC. The CRC-as-cui-image trick is
            // the same one Sign Artist uses for sign rendering — Rust's CuiRawImage.Png
            // field is just a string-ified FileStorage CRC.
            var fullPath = Path.Combine(Interface.Oxide.DataDirectory, "SignArtSaver", art.BytesPath);
            byte[] bytes;
            try { bytes = File.ReadAllBytes(fullPath); }
            catch (Exception e) { player.ChatMessage(Tag() + Err($"Failed to read PNG: {e.Message}")); return; }
            if (bytes == null || bytes.Length == 0) { player.ChatMessage(Tag() + Err("PNG file is empty.")); return; }

            // Tear down any prior preview before opening a new one.
            ClosePreview(player, panel, refreshPanel: false);

            uint crc;
            try
            {
                crc = FileStorage.server.Store(bytes, FileStorage.Type.png, new NetworkableId(0));
            }
            catch (Exception e)
            {
                PrintWarning($"FileStorage.Store for preview failed: {e.Message}");
                player.ChatMessage(Tag() + Err("Couldn't register preview image."));
                return;
            }
            if (crc == 0) { player.ChatMessage(Tag() + Err("Preview image rejected by FileStorage.")); return; }

            panel.PreviewCrc = crc;
            panel.PreviewOwnerId = ownerId;
            panel.PreviewSlot = slot;

            // Hide the main panel (and its backdrop) while the preview is up so the image
            // is on a clean screen instead of bleeding through the semi-transparent modal.
            // RefreshBrowsePanel rebuilds them when the preview closes (refreshPanel: true).
            CuiHelper.DestroyUi(player, UiPanel);
            CuiHelper.DestroyUi(player, UiBackdrop);

            string ownerName = ResolveOwnerName(ownerId, lib);
            BuildPreviewModal(player, art, ownerId, ownerName, crc);
        }

        // Close the preview modal: destroy CUI, free the FileStorage entry.
        [ConsoleCommand("signartsaver.ui.preview.close")]
        private void CcUiPreviewClose(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;
            ClosePreview(player, panel, refreshPanel: true);
        }

        // Apply the slot currently shown in the preview modal — saves the player from
        // closing, finding the row, and clicking Apply again.
        [ConsoleCommand("signartsaver.ui.preview.apply")]
        private void CcUiPreviewApply(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;
            if (panel.PreviewSlot < 1 || panel.PreviewOwnerId == 0) return;

            ulong ownerId = panel.PreviewOwnerId;
            int slot = panel.PreviewSlot;

            if (!store.Players.TryGetValue(ownerId, out var lib))
            {
                player.ChatMessage(Tag() + Err("Owner library not found."));
                return;
            }
            var art = lib.Slots.FirstOrDefault(s => s.Slot == slot);
            if (art == null) { player.ChatMessage(Tag() + Err($"Slot {slot} no longer exists.")); return; }

            // Tear down preview + main panel so the player has a clear view.
            ClosePreview(player, panel, refreshPanel: false);
            DestroyAllUi(player);
            openPanels.Remove(player.userID);

            if (TryRaycastSign(player, out var entity, out var kind, out _))
            {
                ApplyArtToEntity(player, entity, kind, art, ownerId);
                return;
            }

            awaitingApply[player.userID] = new PendingApply
            {
                Slot = slot,
                ArmedUtc = DateTime.UtcNow,
                TargetUserId = ownerId == (ulong)player.userID ? 0UL : ownerId,
            };
            player.ChatMessage(Tag() + Ok(L("SlotArmedNamed", player, slot, EscapeRich(art.Name), config.PendingApplyTimeoutSeconds)));
        }

        [ConsoleCommand("signartsaver.ui.tab")]
        private void CcUiTab(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;
            string t = arg.GetString(0) ?? "";
            int newTab;
            switch (t)
            {
                case "public": newTab = TabPublic; break;
                case "shared": newTab = TabShared; break;
                default:       newTab = TabMine;   break;
            }
            if (panel.Tab == newTab) return;
            panel.Tab = newTab;
            panel.Page = 1;
            panel.PendingRenameSlot = null;
            panel.PendingDeleteSlot = null;
            panel.PendingDeleteArmedUtc = null;
            RefreshBrowsePanel(player);
        }

        // ---- Share modal (v0.10.2) ----
        // Opens a player picker modal. Lists the artist's RelationshipManager contacts
        // (Steam friends + acquaintances + recently-seen players that Rust already tracks
        // for them), with online players sorted to the top. Click [Add] on a row to share.
        // The "Aim at player" fallback button is still available for when the buyer is
        // physically standing right next to the artist.
        [ConsoleCommand("signartsaver.ui.share.open")]
        private void CcUiShareOpen(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            int slot = arg.GetInt(0, -1);
            if (slot < 1) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;
            var lib = store.Players.TryGetValue((ulong)player.userID, out var l) ? l : null;
            var art = lib?.Slots.FirstOrDefault(s => s.Slot == slot);
            if (art == null) { player.ChatMessage(Tag() + Err($"Slot {slot} not found.")); return; }
            if (string.IsNullOrEmpty(art.BytesPath))
            {
                player.ChatMessage(Tag() + Err($"Slot {slot} is URL-only; can't share without bytes."));
                return;
            }
            panel.ShareModalSlot = slot;
            panel.ShareModalPage = 1;
            panel.ShareModalSearch = null;
            BuildShareModal(player);
        }

        [ConsoleCommand("signartsaver.ui.share.close")]
        private void CcUiShareClose(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiShareModal);
            if (openPanels.TryGetValue(player.userID, out var panel))
            {
                panel.ShareModalSlot = 0;
                panel.ShareModalPage = 1;
                panel.ShareModalSearch = null;
                RefreshBrowsePanel(player);
            }
        }

        // Add a buyer from the picker. Args: <slot> <buyerSteamId>. Re-renders the modal
        // so the artist can keep adding more (modal stays open, "Currently shared" updates).
        [ConsoleCommand("signartsaver.ui.share.add")]
        private void CcUiShareAdd(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            if (!int.TryParse(arg.Args[0], out var slot) || slot < 1) return;
            if (!ulong.TryParse(arg.Args[1], out var buyerId) || !buyerId.IsSteamId()) return;
            string buyerName = ResolveDisplayName(buyerId);
            // Reuse the chat-path logic so validation + notify + roster-stamp stay in one place.
            SubShare(player, new[] { "share", slot.ToString(), buyerId.ToString() });
            // Re-render modal (the chat path doesn't re-open it).
            if (openPanels.TryGetValue(player.userID, out var panel) && panel.ShareModalSlot == slot)
            {
                BuildShareModal(player);
            }
        }

        [ConsoleCommand("signartsaver.ui.share.revoke")]
        private void CcUiShareRevoke(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            if (!int.TryParse(arg.Args[0], out var slot) || slot < 1) return;
            if (!ulong.TryParse(arg.Args[1], out var buyerId) || !buyerId.IsSteamId()) return;
            SubUnshare(player, new[] { "unshare", slot.ToString(), buyerId.ToString() });
            if (openPanels.TryGetValue(player.userID, out var panel) && panel.ShareModalSlot == slot)
            {
                BuildShareModal(player);
            }
        }

        [ConsoleCommand("signartsaver.ui.share.page")]
        private void CcUiSharePage(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;
            if (panel.ShareModalSlot < 1) return;
            string dir = arg.GetString(0) ?? "";
            if (dir == "next") panel.ShareModalPage++;
            else if (dir == "prev") panel.ShareModalPage = Math.Max(1, panel.ShareModalPage - 1);
            BuildShareModal(player);
        }

        [ConsoleCommand("signartsaver.ui.share.search")]
        private void CcUiShareSearch(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;
            if (panel.ShareModalSlot < 1) return;
            string q = arg.Args != null && arg.Args.Length >= 1
                ? string.Join(" ", arg.Args).Trim()
                : "";
            if (q.StartsWith("Search ", StringComparison.OrdinalIgnoreCase)) q = q.Substring(7).Trim();
            else if (string.Equals(q, "Search", StringComparison.OrdinalIgnoreCase)) q = "";
            panel.ShareModalSearch = string.IsNullOrEmpty(q) ? null : q;
            panel.ShareModalPage = 1;
            BuildShareModal(player);
        }

        // Build the share-picker candidate list. Always includes currently-connected
        // players + the artist's RelationshipManager contacts (Steam friends, etc.).
        // The full KnownPlayers offline roster is exposed ONLY when an explicit search
        // query (length ≥ 2) is active — without that gate the picker leaks every steamid
        // + display name that has ever connected to anyone with /saveart open. The
        // search-gating limits the leak to names the artist already knows enough to type.
        // relType: 0=None, 1=Acquaintance, 2=Friend, 3=Enemy.
        private List<(ulong steamId, string name, int relType, bool online)> GetPickerCandidates(BasePlayer player, string searchQuery)
        {
            var result = new List<(ulong, string, int, bool)>();
            if (player == null) return result;
            ulong me = (ulong)player.userID;

            // Tags lookup (informational only).
            var tags = new Dictionary<ulong, int>();
            try
            {
                var rmType = typeof(RelationshipManager);
                var serverField = rmType.GetField("ServerInstance", BindingFlags.Public | BindingFlags.Static);
                var server = serverField?.GetValue(null);
                if (server != null)
                {
                    var relsField = rmType.GetField("relationships", BindingFlags.Public | BindingFlags.Instance);
                    var rels = relsField?.GetValue(server) as System.Collections.IDictionary;
                    if (rels != null && rels.Contains(me))
                    {
                        var myRels = rels[me];
                        var relationsField = myRels?.GetType().GetField("relations", BindingFlags.Public | BindingFlags.Instance);
                        var relations = relationsField?.GetValue(myRels) as System.Collections.IDictionary;
                        if (relations != null)
                        {
                            foreach (System.Collections.DictionaryEntry kv in relations)
                            {
                                if (kv.Value == null) continue;
                                ulong sid = Convert.ToUInt64(kv.Key);
                                if (sid == me) continue;
                                int relType = Convert.ToInt32(kv.Value.GetType().GetField("type", BindingFlags.Public | BindingFlags.Instance)?.GetValue(kv.Value) ?? 0);
                                tags[sid] = relType;
                            }
                        }
                    }
                }
            }
            catch { /* tag lookup is optional; swallow */ }

            var seen = new HashSet<ulong>();

            // Primary: all currently-connected players (search target). Includes the caller
            // so the picker isn't empty on solo testing — the row rendering disables the
            // Add button on the caller's row and SubShare double-checks "can't share with
            // yourself" if anyone bypasses the UI guard.
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !p.userID.IsSteamId()) continue;
                ulong sid = (ulong)p.userID;
                string n = !string.IsNullOrEmpty(p.displayName) ? p.displayName : ResolveDisplayName(sid);
                int relType = tags.TryGetValue(sid, out var t) ? t : 0;
                result.Add((sid, n, relType, true));
                seen.Add(sid);
            }

            // Tagged contacts (Steam friends / acquaintances) — always shown even when
            // offline. They're already in the artist's Steam roster, so no privacy leak.
            foreach (var kv in tags)
            {
                if (seen.Contains(kv.Key)) continue;
                if (kv.Value == 0 || kv.Value == 3) continue; // None or Enemy not whitelisted by default
                string n = ResolveDisplayName(kv.Key);
                result.Add((kv.Key, n, kv.Value, false));
                seen.Add(kv.Key);
            }

            // Full roster — only when search query is non-trivial. Matching also restricted
            // to names that actually contain the needle, so the result list never grows
            // larger than the artist's typed query intentionally selects.
            bool searchActive = !string.IsNullOrEmpty(searchQuery) && searchQuery.Trim().Length >= 2;
            if (searchActive)
            {
                string needle = searchQuery.Trim();
                foreach (var kv in store.KnownPlayers)
                {
                    if (seen.Contains(kv.Key)) continue;
                    string n = kv.Value?.Name;
                    if (string.IsNullOrEmpty(n)) continue;
                    if (n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int relType = tags.TryGetValue(kv.Key, out var t) ? t : 0;
                    result.Add((kv.Key, n, relType, false));
                    seen.Add(kv.Key);
                }
            }

            return result;
        }

        // Build (or rebuild) the share-modal CUI for the player's currently-active share
        // session. Reads panel.ShareModalSlot / ShareModalPage / ShareModalSearch.
        private void BuildShareModal(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;
            if (panel.ShareModalSlot < 1) return;
            int slot = panel.ShareModalSlot;
            var lib = store.Players.TryGetValue((ulong)player.userID, out var l) ? l : null;
            var art = lib?.Slots.FirstOrDefault(s => s.Slot == slot);
            if (art == null)
            {
                CuiHelper.DestroyUi(player, UiShareModal);
                panel.ShareModalSlot = 0;
                return;
            }

            // Tear down + rebuild for clean state.
            CuiHelper.DestroyUi(player, UiShareModal);

            var candidates = GetPickerCandidates(player, panel.ShareModalSearch);
            // Search query also filters the inclusive set down for display — the roster
            // expansion above already narrowed offline names; this further narrows online
            // and contact entries by the same needle so the visible list is consistent.
            string q = (panel.ShareModalSearch ?? "").Trim().ToLowerInvariant();
            if (q.Length > 0)
                candidates = candidates.Where(c => c.name.ToLowerInvariant().Contains(q)).ToList();
            // Sort: online first, then friends > acquaintances > none > enemies, then alphabetical.
            candidates.Sort((a, b) =>
            {
                int ao = a.online ? 0 : 1, bo = b.online ? 0 : 1;
                if (ao != bo) return ao - bo;
                // Friend (2) > Acquaintance (1) > None (0) > Enemy (3 — sort to bottom)
                int aRank = a.relType == 3 ? -1 : a.relType;
                int bRank = b.relType == 3 ? -1 : b.relType;
                if (aRank != bRank) return bRank - aRank;
                return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            });

            const int rowsPerPage = 8;
            int totalPages = Math.Max(1, (int)Math.Ceiling(candidates.Count / (double)rowsPerPage));
            if (panel.ShareModalPage > totalPages) panel.ShareModalPage = totalPages;
            if (panel.ShareModalPage < 1) panel.ShareModalPage = 1;
            var pageRows = candidates.Skip((panel.ShareModalPage - 1) * rowsPerPage).Take(rowsPerPage).ToList();

            var sharedSet = new HashSet<ulong>();
            if (art.SharedWith != null) foreach (var s in art.SharedWith) sharedSet.Add(s.SteamId);

            var c = new CuiElementContainer();

            // Backdrop (covers the existing browse panel completely while modal is up).
            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.85" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true,
            }, "Overlay", UiShareModal);

            // Centered card.
            string card = $"{UiShareModal}.Card";
            c.Add(new CuiPanel
            {
                Image = { Color = "0.082 0.082 0.082 1" },
                RectTransform = { AnchorMin = "0.22 0.13", AnchorMax = "0.78 0.87" },
            }, UiShareModal, card);

            // Title bar.
            string titleBar = $"{UiShareModal}.Title";
            c.Add(new CuiPanel
            {
                Image = { Color = "0.122 0.122 0.122 1" },
                RectTransform = { AnchorMin = "0 0.94", AnchorMax = "1 1" },
            }, card, titleBar);
            c.Add(new CuiLabel
            {
                Text = {
                    Text = $"Share “{EscapeRich(art.Name ?? $"slot {slot}")}” with…",
                    FontSize = 16, Font = "robotocondensed-bold.ttf",
                    Align = TextAnchor.MiddleLeft, Color = "1 0.8 0.333 1",
                },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.92 1" },
            }, titleBar);
            c.Add(new CuiButton
            {
                Button = { Color = "0.5 0.15 0.15 1", Command = "signartsaver.ui.share.close" },
                RectTransform = { AnchorMin = "0.94 0.15", AnchorMax = "0.99 0.85" },
                Text = { Text = "✕", Align = TextAnchor.MiddleCenter, FontSize = 16, Color = "1 1 1 1" },
            }, titleBar);

            // === Currently-shared section (top) ===
            string sharedSection = $"{UiShareModal}.Shared";
            c.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.10 0.10 1" },
                RectTransform = { AnchorMin = "0 0.66", AnchorMax = "1 0.94" },
            }, card, sharedSection);
            int sharedCount = art.SharedWith?.Count ?? 0;
            c.Add(new CuiLabel
            {
                Text = {
                    Text = $"<color=#aaaaaa>Currently shared with ({sharedCount}):</color>",
                    FontSize = 13, Font = "robotocondensed-bold.ttf",
                    Align = TextAnchor.MiddleLeft, Color = "1 1 1 1",
                },
                RectTransform = { AnchorMin = "0.02 0.85", AnchorMax = "0.98 1" },
            }, sharedSection);
            if (sharedCount == 0)
            {
                c.Add(new CuiLabel
                {
                    Text = {
                        Text = "<color=#666666>No buyers yet. Add one from the list below.</color>",
                        FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1",
                    },
                    RectTransform = { AnchorMin = "0.04 0.0", AnchorMax = "0.98 0.85" },
                }, sharedSection);
            }
            else
            {
                // Show up to 4 buyers in this row band. (More are accessible via /saveart shared <slot>.)
                var orderedShares = art.SharedWith.OrderByDescending(s => s.SharedUtc).Take(4).ToList();
                float rowH = 0.85f / 4f;
                for (int i = 0; i < orderedShares.Count; i++)
                {
                    var entry = orderedShares[i];
                    float yMax = 0.85f - i * rowH;
                    float yMin = yMax - rowH;
                    string display = ResolveDisplayName(entry.SteamId);
                    string ageStr = FormatAge(entry.SharedUtc);
                    c.Add(new CuiLabel
                    {
                        Text = {
                            Text = $"  • <color=#7ad>{EscapeRich(display)}</color>  <color=#888>(shared {ageStr})</color>",
                            FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1",
                        },
                        RectTransform = { AnchorMin = $"0.02 {yMin}", AnchorMax = $"0.85 {yMax}" },
                    }, sharedSection);
                    c.Add(new CuiButton
                    {
                        Button = { Color = "0.50 0.15 0.15 1", Command = $"signartsaver.ui.share.revoke {slot} {entry.SteamId}" },
                        RectTransform = { AnchorMin = $"0.86 {yMin + 0.01f}", AnchorMax = $"0.98 {yMax - 0.01f}" },
                        Text = { Text = "Revoke", Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
                    }, sharedSection);
                }
            }

            // === Search bar ===
            string searchBg = $"{UiShareModal}.SearchBg";
            c.Add(new CuiPanel
            {
                Image = { Color = "0.122 0.122 0.122 1" },
                RectTransform = { AnchorMin = "0 0.59", AnchorMax = "1 0.65" },
            }, card, searchBg);
            c.Add(new CuiLabel
            {
                Text = {
                    Text = $"<color=#aaaaaa>Search players  <color=#888>({candidates.Count} found)</color>:</color>",
                    FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1",
                    Font = "robotocondensed-bold.ttf",
                },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.40 1" },
            }, searchBg);
            bool isSearchPlaceholder = string.IsNullOrEmpty(panel.ShareModalSearch);
            c.Add(new CuiElement
            {
                Name = $"{UiShareModal}.SearchInput",
                Parent = searchBg,
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = isSearchPlaceholder ? "Search by name…" : panel.ShareModalSearch,
                        FontSize = 12, CharsLimit = 40,
                        Align = TextAnchor.MiddleLeft,
                        Color = isSearchPlaceholder ? "0.5 0.5 0.5 1" : "1 1 1 1",
                        Command = "signartsaver.ui.share.search",
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.41 0.15", AnchorMax = "0.86 0.85" },
                },
            });
            if (!isSearchPlaceholder)
            {
                c.Add(new CuiButton
                {
                    Button = { Color = "0.30 0.10 0.10 1", Command = "signartsaver.ui.share.search" },
                    RectTransform = { AnchorMin = "0.88 0.20", AnchorMax = "0.98 0.80" },
                    Text = { Text = "Clear", Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
                }, searchBg);
            }

            // === Player list ===
            string listSection = $"{UiShareModal}.List";
            c.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.10 0.10 1" },
                RectTransform = { AnchorMin = "0 0.10", AnchorMax = "1 0.59" },
            }, card, listSection);
            if (pageRows.Count == 0)
            {
                c.Add(new CuiLabel
                {
                    Text = {
                        Text = q.Length > 0 ? $"<color=#aaaaaa>No players match “{EscapeRich(panel.ShareModalSearch)}”.</color>"
                                            : "<color=#aaaaaa>No players found. Wait for someone to connect, or use the Aim option below if the buyer is right next to you.</color>",
                        FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1",
                    },
                    RectTransform = { AnchorMin = "0.05 0.30", AnchorMax = "0.95 0.70" },
                }, listSection);
            }
            else
            {
                float rowH = 1.0f / rowsPerPage;
                ulong meId = (ulong)player.userID;
                for (int i = 0; i < pageRows.Count; i++)
                {
                    var (sid, name, _, _) = pageRows[i];
                    bool isSelf = sid == meId;
                    float yMax = 1f - i * rowH;
                    float yMin = yMax - rowH;
                    string rowId = $"{listSection}.Row.{i}";
                    c.Add(new CuiPanel
                    {
                        Image = { Color = (i % 2 == 0) ? "0.149 0.149 0.149 1" : "0.118 0.118 0.118 1" },
                        RectTransform = { AnchorMin = $"0 {yMin}", AnchorMax = $"1 {yMax}" },
                    }, listSection, rowId);

                    // Privacy: show only the player name. Online/offline + relationship type
                    // are still used internally for sort priority but never displayed.
                    string nameDisplay = isSelf
                        ? $"<color=#ffe699>{EscapeRich(name)}</color> <color=#888>(you)</color>"
                        : $"<color=#ffe699>{EscapeRich(name)}</color>";
                    c.Add(new CuiLabel
                    {
                        Text = {
                            Text = "  " + nameDisplay,
                            FontSize = 13, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1",
                        },
                        RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.84 1" },
                    }, rowId);

                    bool already = sharedSet.Contains(sid);
                    bool disabled = already || isSelf;
                    c.Add(new CuiButton
                    {
                        Button = {
                            Color = disabled ? "0.20 0.20 0.20 1" : "0.20 0.45 0.20 1",
                            Command = disabled ? "" : $"signartsaver.ui.share.add {slot} {sid}",
                        },
                        RectTransform = { AnchorMin = "0.86 0.18", AnchorMax = "0.98 0.82" },
                        Text = {
                            Text = isSelf ? "—" : (already ? "Added" : "Add"),
                            Align = TextAnchor.MiddleCenter, FontSize = 12, Color = "1 1 1 1",
                        },
                    }, rowId);
                }
            }

            // === Footer (pagination + Aim option + Done) ===
            string footer = $"{UiShareModal}.Footer";
            c.Add(new CuiPanel
            {
                Image = { Color = "0.122 0.122 0.122 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.10" },
            }, card, footer);

            c.Add(new CuiButton
            {
                Button = {
                    Color = panel.ShareModalPage > 1 ? "0.20 0.20 0.20 1" : "0.12 0.12 0.12 1",
                    Command = panel.ShareModalPage > 1 ? "signartsaver.ui.share.page prev" : "",
                },
                RectTransform = { AnchorMin = "0.02 0.15", AnchorMax = "0.13 0.85" },
                Text = { Text = "◄ Prev", Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
            }, footer);
            c.Add(new CuiLabel
            {
                Text = { Text = $"Page {panel.ShareModalPage}/{totalPages}", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.14 0", AnchorMax = "0.30 1" },
            }, footer);
            c.Add(new CuiButton
            {
                Button = {
                    Color = panel.ShareModalPage < totalPages ? "0.20 0.20 0.20 1" : "0.12 0.12 0.12 1",
                    Command = panel.ShareModalPage < totalPages ? "signartsaver.ui.share.page next" : "",
                },
                RectTransform = { AnchorMin = "0.31 0.15", AnchorMax = "0.42 0.85" },
                Text = { Text = "Next ►", Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
            }, footer);

            c.Add(new CuiButton
            {
                Button = { Color = "0.20 0.30 0.45 1", Command = "signartsaver.ui.share.close" },
                RectTransform = { AnchorMin = "0.80 0.15", AnchorMax = "0.98 0.85" },
                Text = { Text = "Done", Align = TextAnchor.MiddleCenter, FontSize = 12, Color = "1 1 1 1" },
            }, footer);

            CuiHelper.AddUi(player, c);
        }

        [ConsoleCommand("signartsaver.ui.publish.toggle")]
        private void CcUiPublishToggle(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            int slot = arg.GetInt(0, -1);
            if (slot < 1) return;
            // Shares logic with the chat /saveart publish path (own library only — admin
            // re-dispatch goes through /saveart admin).
            SubPublish(player, new[] { "publish", slot.ToString() }, 0UL);
        }

        [ConsoleCommand("signartsaver.ui.rename.start")]
        private void CcUiRenameStart(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            int slot = arg.GetInt(0, -1);
            if (slot < 1) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;
            panel.PendingRenameSlot = slot;
            // Cancel any in-flight delete confirmation on this slot to avoid mixed states.
            if (panel.PendingDeleteSlot == slot)
            {
                panel.PendingDeleteSlot = null;
                panel.PendingDeleteArmedUtc = null;
            }
            RefreshBrowsePanel(player);
        }

        [ConsoleCommand("signartsaver.ui.rename.commit")]
        private void CcUiRenameCommit(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            int slot = arg.GetInt(0, -1);
            if (slot < 1) return;
            // Args[0] = slot, Args[1..] = the typed name (CuiInputField appends after Command).
            string newName = arg.Args != null && arg.Args.Length >= 2
                ? string.Join(" ", arg.Args.Skip(1)).Trim()
                : "";
            // Empty submit → cancel the rename, keep existing name.
            if (string.IsNullOrEmpty(newName))
            {
                if (openPanels.TryGetValue(player.userID, out var p)) p.PendingRenameSlot = null;
                RefreshBrowsePanel(player);
                return;
            }
            // Reuse the chat path so validation + lib lookup logic stays in one place.
            SubRename(player, new[] { "rename", slot.ToString(), newName }, 0UL);
            if (openPanels.TryGetValue(player.userID, out var panel)) panel.PendingRenameSlot = null;
            RefreshBrowsePanel(player);
        }

        [ConsoleCommand("signartsaver.ui.search.set")]
        private void CcUiSearchSet(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;
            string q = arg.Args != null && arg.Args.Length >= 1
                ? string.Join(" ", arg.Args).Trim()
                : "";
            // Strip the placeholder prefix if Unity's TMP_InputField didn't select-all on focus
            // and the user typed alongside the placeholder text.
            if (q.StartsWith("Search ", StringComparison.OrdinalIgnoreCase)) q = q.Substring(7).Trim();
            else if (string.Equals(q, "Search", StringComparison.OrdinalIgnoreCase)) q = "";
            panel.PublicSearchQuery = string.IsNullOrEmpty(q) ? null : q;
            panel.Page = 1;
            RefreshBrowsePanel(player);
        }

        [ConsoleCommand("signartsaver.ui.delete.confirm")]
        private void CcUiDeleteConfirm(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !HasUse(player)) return;
            int slot = arg.GetInt(0, -1);
            if (slot < 1) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;
            if (panel.PendingDeleteSlot != slot ||
                !panel.PendingDeleteArmedUtc.HasValue ||
                (DateTime.UtcNow - panel.PendingDeleteArmedUtc.Value).TotalSeconds > config.ConfirmDeleteSeconds)
            {
                player.ChatMessage(Tag() + Warn("Confirm window expired — click Delete again."));
                panel.PendingDeleteSlot = null;
                panel.PendingDeleteArmedUtc = null;
                RefreshBrowsePanel(player);
                return;
            }

            ulong target = panel.AdminTargetId != 0 ? panel.AdminTargetId : (ulong)player.userID;
            var lib = store.Players.TryGetValue(target, out var l) ? l : null;
            int idx = lib?.Slots.FindIndex(s => s.Slot == slot) ?? -1;
            if (idx < 0) { player.ChatMessage(Tag() + Err($"Slot {slot} no longer exists.")); panel.PendingDeleteSlot = null; panel.PendingDeleteArmedUtc = null; RefreshBrowsePanel(player); return; }
            DeleteSlotPng(lib.Slots[idx]);
            lib.Slots.RemoveAt(idx);
            StampLib(lib);
            panel.PendingDeleteSlot = null;
            panel.PendingDeleteArmedUtc = null;
            player.ChatMessage(Tag() + Ok($"Removed slot {slot}."));
            RefreshBrowsePanel(player);
        }

        #endregion

        #region CUI

        private void OpenBrowsePanel(BasePlayer player, ulong adminTargetId)
        {
            if (player == null || !player.IsConnected) return;
            DestroyAllUi(player);
            openPanels[player.userID] = new BrowsePanel { Page = 1, Tab = TabMine, AdminTargetId = adminTargetId };
            RefreshBrowsePanel(player);
        }

        // Layout (top-down, in panel-relative coords):
        //   0.93 - 1.00  Title bar + close
        //   0.86 - 0.92  Tab strip [ My Library ][ Public Gallery ]
        //   0.79 - 0.85  Search bar (Public tab only)
        //   X    - Y     Column header (mine: 0.79-0.85, public: 0.72-0.78)
        //   0.10 - X     Rows
        //   0.04 - 0.10  Pagination
        //   0.00 - 0.04  Status / hint line
        private void RefreshBrowsePanel(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            if (!openPanels.TryGetValue(player.userID, out var panel)) return;

            // Tab-specific row source.
            List<(SavedArt art, ulong ownerId, string ownerName)> allRows;
            int totalSlotsForHeader;     // count shown in title (mine: own slots; public: filtered count)
            int slotCapForHeader;        // cap shown in title (mine: SlotsPerPlayer; public: -1 = hidden)
            ulong libOwnerForMine = panel.AdminTargetId != 0 ? panel.AdminTargetId : (ulong)player.userID;
            string mineOwnerName = null;

            if (panel.Tab == TabPublic)
            {
                slotCapForHeader = -1;
                string q = (panel.PublicSearchQuery ?? "").Trim().ToLowerInvariant();
                var collected = new List<(SavedArt, ulong, string)>();
                foreach (var kv in store.Players)
                {
                    var lib2 = kv.Value;
                    if (lib2?.Slots == null) continue;
                    string ownerName = ResolveOwnerName(kv.Key, lib2);
                    foreach (var a in lib2.Slots)
                    {
                        if (!a.IsPublic) continue;
                        if (string.IsNullOrEmpty(a.BytesPath)) continue; // bytes are required to publish
                        if (q.Length > 0)
                        {
                            string nameL = (a.Name ?? "").ToLowerInvariant();
                            string ownerL = ownerName.ToLowerInvariant();
                            string canvasL = (a.OriginalCanvasName ?? "").ToLowerInvariant();
                            if (!nameL.Contains(q) && !ownerL.Contains(q) && !canvasL.Contains(q)) continue;
                        }
                        collected.Add((a, kv.Key, ownerName));
                    }
                }
                // Newest first — published artwork is mostly fresh, recency wins.
                collected.Sort((x, y) =>
                {
                    var xt = x.Item1.PublishedUtc ?? x.Item1.SavedUtc;
                    var yt = y.Item1.PublishedUtc ?? y.Item1.SavedUtc;
                    return yt.CompareTo(xt);
                });
                allRows = collected;
                totalSlotsForHeader = collected.Count;
            }
            else if (panel.Tab == TabShared)
            {
                // Shared-with-me: every slot from every artist where caller is on SharedWith.
                slotCapForHeader = -1;
                ulong me = (ulong)player.userID;
                var collected = new List<(SavedArt, ulong, string)>();
                foreach (var kv in store.Players)
                {
                    var lib2 = kv.Value;
                    if (lib2?.Slots == null) continue;
                    string ownerName = ResolveOwnerName(kv.Key, lib2);
                    foreach (var a in lib2.Slots)
                    {
                        if (a.SharedWith == null || a.SharedWith.Count == 0) continue;
                        if (!a.SharedWith.Any(s => s.SteamId == me)) continue;
                        if (string.IsNullOrEmpty(a.BytesPath)) continue;
                        collected.Add((a, kv.Key, ownerName));
                    }
                }
                // Newest-share-first so fresh commissions surface at the top.
                collected.Sort((x, y) =>
                {
                    var xs = x.Item1.SharedWith.First(s => s.SteamId == me).SharedUtc;
                    var ys = y.Item1.SharedWith.First(s => s.SteamId == me).SharedUtc;
                    return ys.CompareTo(xs);
                });
                allRows = collected;
                totalSlotsForHeader = collected.Count;
            }
            else
            {
                slotCapForHeader = config.SlotsPerPlayer;
                var lib = store.Players.TryGetValue(libOwnerForMine, out var l) ? l : null;
                mineOwnerName = lib?.OwnerName;
                allRows = (lib?.Slots ?? new List<SavedArt>())
                    .OrderBy(s => s.Slot)
                    .Select(a => (a, libOwnerForMine, mineOwnerName))
                    .ToList();
                totalSlotsForHeader = allRows.Count;
            }

            int pageSize = config.RowsPerPage;
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)allRows.Count / pageSize));
            if (panel.Page > totalPages) panel.Page = totalPages;
            if (panel.Page < 1) panel.Page = 1;
            var rows = allRows.Skip((panel.Page - 1) * pageSize).Take(pageSize).ToList();

            var c = new CuiElementContainer();

            // Fullscreen opaque backdrop — sits behind the centered card and hides any
            // game HUD elements / other plugin overlays (hotbar overlays, etc.) that would
            // otherwise show through the corners. Sibling of UiPanel; both are destroyed
            // together. CursorEnabled=false on this element so the cursor lock is owned by
            // UiPanel only.
            c.Add(new CuiPanel
            {
                Image = { Color = "0.04 0.04 0.04 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
            }, "Overlay", UiBackdrop);

            // Outer panel — centered card on top of the backdrop.
            c.Add(new CuiPanel
            {
                Image = { Color = "0.082 0.082 0.082 1" },
                RectTransform = { AnchorMin = "0.13 0.16", AnchorMax = "0.87 0.88" },
                CursorEnabled = true,
            }, "Overlay", UiPanel);

            // === Header strip ===
            c.Add(new CuiPanel
            {
                Image = { Color = "0.122 0.122 0.122 1" },
                RectTransform = { AnchorMin = "0 0.93", AnchorMax = "1 1" },
            }, UiPanel, UiHeader);

            string titleSuffix = panel.AdminTargetId != 0 ? $" (admin view: {ResolveOwnerName(panel.AdminTargetId)})" : "";
            string title;
            if (panel.Tab == TabPublic)
                title = $"PUBLIC GALLERY — {totalSlotsForHeader} {(totalSlotsForHeader == 1 ? "slot" : "slots")}";
            else if (panel.Tab == TabShared)
                title = $"SHARED WITH ME — {totalSlotsForHeader} {(totalSlotsForHeader == 1 ? "slot" : "slots")}";
            else
                title = $"MY LIBRARY — {totalSlotsForHeader}/{slotCapForHeader}" + titleSuffix;

            c.Add(new CuiLabel
            {
                Text = {
                    Text = title,
                    FontSize = 16,
                    Font = "robotocondensed-bold.ttf",
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 0.8 0.333 1",
                },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.92 1" },
            }, UiHeader, UiTitle);

            c.Add(new CuiButton
            {
                Button = { Color = "0.5 0.15 0.15 1", Command = "signartsaver.ui.close" },
                RectTransform = { AnchorMin = "0.94 0.15", AnchorMax = "0.99 0.85" },
                Text = { Text = "✕", Align = TextAnchor.MiddleCenter, FontSize = 16, Color = "1 1 1 1" },
            }, UiHeader, UiCloseBtn);

            // === Tab strip ===
            c.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.10 0.10 1" },
                RectTransform = { AnchorMin = "0 0.86", AnchorMax = "1 0.92" },
            }, UiPanel, UiTabs);

            AddTabButton(c, "My Library",      0.02f, 0.18f, panel.Tab == TabMine,   "signartsaver.ui.tab mine");
            AddTabButton(c, "Shared with me",  0.19f, 0.36f, panel.Tab == TabShared, "signartsaver.ui.tab shared");
            AddTabButton(c, "Public Gallery",  0.37f, 0.54f, panel.Tab == TabPublic, "signartsaver.ui.tab public");

            // Toolbar (right side of tab strip): Save sign + Help on both tabs; Wipe library
            // on My Library only — too destructive to expose while browsing the public feed.
            // Wipe two-stage uses the existing wipeArmedUtc dict so chat /saveart wipe and
            // the panel button share the same arm/confirm timer.
            bool wipeArmed = wipeArmedUtc.TryGetValue((ulong)player.userID, out var wat) &&
                             (DateTime.UtcNow - wat).TotalSeconds < config.WipeConfirmSeconds;

            if (panel.Tab == TabMine)
            {
                AddTabButton(c, "Save Sign",    0.55f, 0.66f, false, "signartsaver.ui.save");
                AddTabButton(c, "Import URL",   0.67f, 0.78f, false, "signartsaver.ui.import.start");
                // Wipe button — flips to bright red CONFIRM WIPE while armed.
                c.Add(new CuiButton
                {
                    Button = {
                        Color = wipeArmed ? "0.70 0.10 0.10 1" : "0.30 0.10 0.10 1",
                        Command = wipeArmed ? "signartsaver.ui.wipe.confirm" : "signartsaver.ui.wipe",
                    },
                    RectTransform = { AnchorMin = "0.79 0.10", AnchorMax = "0.91 0.90" },
                    Text = {
                        Text = wipeArmed ? "CONFIRM WIPE" : "Wipe Library",
                        Align = TextAnchor.MiddleCenter,
                        FontSize = 12, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1",
                    },
                }, UiTabs);
                AddTabButton(c, "Help",         0.92f, 0.99f, false, "signartsaver.ui.help");
            }
            else
            {
                AddTabButton(c, "Help",        0.92f, 0.99f, false, "signartsaver.ui.help");
            }

            // Tab-aware vertical anchors for the rest of the layout.
            float colHeaderTop, colHeaderBottom, rowsTop;
            if (panel.Tab == TabPublic)
            {
                // Search bar between tabs and column header.
                AddSearchBar(c, panel.PublicSearchQuery, totalSlotsForHeader);
                colHeaderTop = 0.78f;
                colHeaderBottom = 0.72f;
                rowsTop = 0.72f;
            }
            else
            {
                colHeaderTop = 0.85f;
                colHeaderBottom = 0.79f;
                rowsTop = 0.79f;
            }

            // === Column header ===
            c.Add(new CuiPanel
            {
                Image = { Color = "0.165 0.165 0.165 1" },
                RectTransform = { AnchorMin = $"0 {colHeaderBottom}", AnchorMax = $"1 {colHeaderTop}" },
            }, UiPanel, UiColHeader);

            if (panel.Tab == TabPublic)
            {
                AddColLabel(c, UiColHeader, "Slot",   "0.02", "0.05", false);
                AddColLabel(c, UiColHeader, "Name",   "0.05", "0.22", false);
                AddColLabel(c, UiColHeader, "Canvas", "0.22", "0.42", false);
                AddColLabel(c, UiColHeader, "Artist", "0.42", "0.62", false);
                AddColLabel(c, UiColHeader, "Saved",  "0.62", "0.78", false);
                AddColLabel(c, UiColHeader, "Action", "0.78", "0.99", true);
            }
            else if (panel.Tab == TabShared)
            {
                AddColLabel(c, UiColHeader, "Slot",   "0.02", "0.05", false);
                AddColLabel(c, UiColHeader, "Name",   "0.05", "0.22", false);
                AddColLabel(c, UiColHeader, "Canvas", "0.22", "0.42", false);
                AddColLabel(c, UiColHeader, "Artist", "0.42", "0.62", false);
                AddColLabel(c, UiColHeader, "Shared", "0.62", "0.78", false);
                AddColLabel(c, UiColHeader, "Action", "0.78", "0.99", true);
            }
            else
            {
                AddColLabel(c, UiColHeader, "Slot",   "0.02", "0.05", false);
                AddColLabel(c, UiColHeader, "Name",   "0.05", "0.24", false);
                AddColLabel(c, UiColHeader, "Canvas", "0.24", "0.43", false);
                AddColLabel(c, UiColHeader, "Saved",  "0.43", "0.52", false);
                AddColLabel(c, UiColHeader, "Public", "0.525","0.575",true);
                AddColLabel(c, UiColHeader, "Actions","0.58", "0.99", true);
            }

            // === Rows ===
            const float rowsBottom = 0.10f;
            if (allRows.Count == 0)
            {
                string emptyText = panel.Tab == TabPublic
                    ? "No public art yet. <color=#ffff66>/saveart publish [slot]</color> to share your work."
                    : "Your library is empty.\n<color=#ffff66>/saveart save</color> the painted sign you're aiming at, or <color=#ffff66>/sil [url]</color> via Sign Artist to auto-capture.";
                c.Add(new CuiLabel
                {
                    Text = {
                        Text = emptyText,
                        FontSize = 14, Font = "robotocondensed-regular.ttf",
                        Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1",
                    },
                    RectTransform = { AnchorMin = $"0.05 {rowsBottom + 0.20f}", AnchorMax = $"0.95 {rowsTop - 0.05f}" },
                }, UiPanel, UiEmptyHint);
            }
            else
            {
                float rowH = (rowsTop - rowsBottom) / pageSize;
                for (int i = 0; i < rows.Count; i++)
                {
                    float yMax = rowsTop - i * rowH;
                    float yMin = yMax - rowH;
                    var (art, ownerId, ownerName) = rows[i];
                    if (panel.Tab == TabPublic)
                        AddPublicRow(c, art, ownerId, ownerName, i, yMin, yMax);
                    else if (panel.Tab == TabShared)
                        AddSharedRow(c, art, ownerId, ownerName, (ulong)player.userID, i, yMin, yMax);
                    else
                        AddMineRow(c, art, i, panel, yMin, yMax);
                }
            }

            // === Pagination ===
            c.Add(new CuiPanel
            {
                Image = { Color = "0.122 0.122 0.122 1" },
                RectTransform = { AnchorMin = "0 0.04", AnchorMax = "1 0.10" },
            }, UiPanel, UiPagination);

            c.Add(new CuiButton
            {
                Button = {
                    Color = panel.Page > 1 ? "0.20 0.20 0.20 1" : "0.12 0.12 0.12 1",
                    Command = panel.Page > 1 ? "signartsaver.ui.page prev" : "",
                },
                RectTransform = { AnchorMin = "0.05 0.10", AnchorMax = "0.18 0.90" },
                Text = { Text = "◄ Prev", Align = TextAnchor.MiddleCenter, FontSize = 12, Color = "1 1 1 1" },
            }, UiPagination);

            c.Add(new CuiLabel
            {
                Text = { Text = $"Page {panel.Page} / {totalPages}",
                         FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.40 0", AnchorMax = "0.60 1" },
            }, UiPagination);

            c.Add(new CuiButton
            {
                Button = {
                    Color = panel.Page < totalPages ? "0.20 0.20 0.20 1" : "0.12 0.12 0.12 1",
                    Command = panel.Page < totalPages ? "signartsaver.ui.page next" : "",
                },
                RectTransform = { AnchorMin = "0.82 0.10", AnchorMax = "0.95 0.90" },
                Text = { Text = "Next ►", Align = TextAnchor.MiddleCenter, FontSize = 12, Color = "1 1 1 1" },
            }, UiPagination);

            // === Status / hint line ===
            string statusText;
            if (panel.Tab == TabPublic)
                statusText = "<color=#aaaaaa>Apply any public slot to <color=#ffcc55>your own</color> sign — bytes auto-resize to the canvas. Search by name, artist, or canvas type.</color>";
            else if (panel.Tab == TabShared)
                statusText = "<color=#aaaaaa>Slots other artists have shared with you. Click <color=#ffcc55>Apply</color> then aim + USE on your own sign. Artists can revoke access at any time.</color>";
            else
                statusText = "<color=#aaaaaa>Click <color=#ffcc55>Apply</color> then aim + USE. <color=#ffcc55>Share</color> grants a buyer copy access — they can apply to their own signs (commission/sell flow).</color>";
            c.Add(new CuiLabel
            {
                Text = {
                    Text = statusText,
                    FontSize = 11, Font = "robotocondensed-regular.ttf",
                    Align = TextAnchor.MiddleCenter, Color = "1 1 1 1",
                },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.04" },
            }, UiPanel, UiStatus);

            CuiHelper.DestroyUi(player, UiPanel);
            CuiHelper.DestroyUi(player, UiBackdrop);
            CuiHelper.AddUi(player, c);
        }

        // === Tab strip helper ===
        private void AddTabButton(CuiElementContainer c, string label, float xMin, float xMax, bool active, string command)
        {
            c.Add(new CuiButton
            {
                Button = {
                    Color = active ? "0.20 0.45 0.20 1" : "0.18 0.18 0.18 1",
                    Command = active ? "" : command,
                },
                RectTransform = { AnchorMin = $"{xMin} 0.10", AnchorMax = $"{xMax} 0.90" },
                Text = {
                    Text = label,
                    Align = TextAnchor.MiddleCenter,
                    FontSize = 13,
                    Font = "robotocondensed-bold.ttf",
                    Color = active ? "1 1 1 1" : "0.75 0.75 0.75 1",
                },
            }, UiTabs);
        }

        // === Search bar (Public tab only) ===
        private void AddSearchBar(CuiElementContainer c, string query, int matchCount)
        {
            c.Add(new CuiPanel
            {
                Image = { Color = "0.122 0.122 0.122 1" },
                RectTransform = { AnchorMin = "0 0.79", AnchorMax = "1 0.85" },
            }, UiPanel, UiSearchBg);

            // Match-count label (left of the input).
            c.Add(new CuiLabel
            {
                Text = {
                    Text = $"<color=#a0a0a0>{matchCount} match{(matchCount == 1 ? "" : "es")}</color>",
                    FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1",
                    Font = "robotocondensed-regular.ttf",
                },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.20 1" },
            }, UiSearchBg);

            // Input field. Placeholder pattern cribbed from RaidWindow's TZ picker.
            bool isPlaceholder = string.IsNullOrEmpty(query);
            c.Add(new CuiElement
            {
                Name = UiSearchInput,
                Parent = UiSearchBg,
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = isPlaceholder ? "Search by name, artist, or canvas…" : query,
                        FontSize = 13, CharsLimit = 40,
                        Align = TextAnchor.MiddleLeft,
                        Color = isPlaceholder ? "0.5 0.5 0.5 1" : "1 1 1 1",
                        Command = "signartsaver.ui.search.set",
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.22 0.15", AnchorMax = "0.78 0.85" },
                },
            });

            // Clear button (right of input).
            if (!string.IsNullOrEmpty(query))
            {
                c.Add(new CuiButton
                {
                    Button = { Color = "0.30 0.10 0.10 1", Command = "signartsaver.ui.search.set" },
                    RectTransform = { AnchorMin = "0.80 0.20", AnchorMax = "0.86 0.80" },
                    Text = { Text = "Clear", Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
                }, UiSearchBg);
            }
        }

        // === My Library row ===
        private void AddMineRow(CuiElementContainer c, SavedArt art, int row, BrowsePanel panel, float yMin, float yMax)
        {
            string rowId = $"SignArtSaver.Ui.Row.{row}";
            c.Add(new CuiPanel
            {
                Image = { Color = (row % 2 == 0) ? "0.149 0.149 0.149 1" : "0.118 0.118 0.118 1" },
                RectTransform = { AnchorMin = $"0 {yMin}", AnchorMax = $"1 {yMax}" },
            }, UiPanel, rowId);

            // Build the canvas display as two pieces: a user-supplied (must-escape) name,
            // followed by a plugin-generated (safe-as-rich-text) dimension tag in dimmer
            // gray. EscapeRich on the whole string was mangling our own <color> tags into
            // visible text — that's the bug 0.4.2 fixes.
            string canvasName = !string.IsNullOrEmpty(art.OriginalCanvasName) ? art.OriginalCanvasName : (art.EntityKind ?? "?");
            string canvasDims = art.OriginalImageWidth > 0 && art.OriginalImageHeight > 0
                ? $"  <color=#888>{art.OriginalImageWidth}×{art.OriginalImageHeight}</color>"
                : "";
            string canvasDisplay = EscapeRich(canvasName) + canvasDims;

            AddRowLabel(c, rowId, art.Slot.ToString(), "0.02", "0.05", "1 1 1 1");

            // Inline rename input field replaces the Name label when this row is armed.
            if (panel.PendingRenameSlot == art.Slot)
            {
                c.Add(new CuiPanel
                {
                    Image = { Color = "0.20 0.20 0.10 1" },
                    RectTransform = { AnchorMin = "0.05 0.15", AnchorMax = "0.24 0.85" },
                }, rowId);
                c.Add(new CuiElement
                {
                    Name = $"{rowId}.RenameInput",
                    Parent = rowId,
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Text = art.Name ?? "",
                            FontSize = 13, CharsLimit = 32,
                            Align = TextAnchor.MiddleLeft, Color = "1 1 1 1",
                            Command = $"signartsaver.ui.rename.commit {art.Slot}",
                        },
                        new CuiRectTransformComponent { AnchorMin = "0.06 0.18", AnchorMax = "0.23 0.82" },
                    },
                });
            }
            else
            {
                AddRowLabel(c, rowId, EscapeRich(art.Name ?? ""), "0.05", "0.24", "1 0.95 0.6 1");
            }

            AddRowLabel(c, rowId, canvasDisplay, "0.24", "0.43", "0.85 0.85 0.85 1");
            AddRowLabel(c, rowId, FormatAge(art.SavedUtc), "0.43", "0.52", "0.85 0.85 0.85 1");

            // Public/Private toggle button.
            bool canPublish = !string.IsNullOrEmpty(art.BytesPath);
            c.Add(new CuiButton
            {
                Button = {
                    Color = !canPublish ? "0.20 0.20 0.20 1"
                          : (art.IsPublic ? "0.30 0.50 0.20 1" : "0.20 0.20 0.30 1"),
                    Command = canPublish ? $"signartsaver.ui.publish.toggle {art.Slot}" : "",
                },
                RectTransform = { AnchorMin = "0.525 0.18", AnchorMax = "0.575 0.82" },
                Text = {
                    Text = !canPublish ? "—" : (art.IsPublic ? "Public" : "Private"),
                    Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1",
                },
            }, rowId);

            // View / Apply / Share / Rename / Delete buttons. View only enabled when we have
            // bytes to render — URL-only slots can't preview without re-downloading the URL,
            // which is bandwidth-wasteful and would defeat the offline-storage purpose.
            ulong libUserIdForRow = panel.AdminTargetId != 0 ? panel.AdminTargetId : (ulong)0;
            bool canView = !string.IsNullOrEmpty(art.BytesPath);
            c.Add(new CuiButton
            {
                Button = {
                    Color = canView ? "0.20 0.40 0.55 1" : "0.20 0.20 0.20 1",
                    Command = canView ? $"signartsaver.ui.preview {libUserIdForRow} {art.Slot}" : "",
                },
                RectTransform = { AnchorMin = "0.580 0.18", AnchorMax = "0.640 0.82" },
                Text = { Text = "View", Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
            }, rowId);

            c.Add(new CuiButton
            {
                Button = { Color = "0.20 0.45 0.20 1", Command = $"signartsaver.ui.apply {art.Slot}" },
                RectTransform = { AnchorMin = "0.645 0.18", AnchorMax = "0.715 0.82" },
                Text = { Text = "Apply", Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
            }, rowId);

            // Share button (v0.10.1) — arms share-by-aim. Disabled for URL-only slots
            // (same constraint as Publish — bytes are required for cross-player apply).
            // Label includes the buyer count so artists can see at a glance which slots
            // are commissioned.
            int shareCount = art.SharedWith?.Count ?? 0;
            bool canShare = !string.IsNullOrEmpty(art.BytesPath);
            string shareLabel = shareCount > 0 ? $"Share ({shareCount})" : "Share";
            c.Add(new CuiButton
            {
                Button = {
                    Color = !canShare ? "0.20 0.20 0.20 1"
                          : (shareCount > 0 ? "0.45 0.30 0.55 1" : "0.30 0.25 0.45 1"),
                    Command = canShare ? $"signartsaver.ui.share.open {art.Slot}" : "",
                },
                RectTransform = { AnchorMin = "0.720 0.18", AnchorMax = "0.790 0.82" },
                Text = { Text = !canShare ? "—" : shareLabel, Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
            }, rowId);

            bool renaming = panel.PendingRenameSlot == art.Slot;
            c.Add(new CuiButton
            {
                Button = {
                    Color = renaming ? "0.45 0.40 0.20 1" : "0.20 0.30 0.45 1",
                    Command = renaming ? $"signartsaver.ui.rename.commit {art.Slot}" : $"signartsaver.ui.rename.start {art.Slot}",
                },
                RectTransform = { AnchorMin = "0.795 0.18", AnchorMax = "0.870 0.82" },
                Text = { Text = renaming ? "Save" : "Rename", Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
            }, rowId);

            bool armed = panel.PendingDeleteSlot == art.Slot &&
                         panel.PendingDeleteArmedUtc.HasValue &&
                         (DateTime.UtcNow - panel.PendingDeleteArmedUtc.Value).TotalSeconds < config.ConfirmDeleteSeconds;
            c.Add(new CuiButton
            {
                Button = {
                    Color = armed ? "0.70 0.10 0.10 1" : "0.30 0.10 0.10 1",
                    Command = armed ? $"signartsaver.ui.delete.confirm {art.Slot}" : $"signartsaver.ui.delete {art.Slot}",
                },
                RectTransform = { AnchorMin = "0.875 0.18", AnchorMax = "0.985 0.82" },
                Text = { Text = armed ? "CONFIRM" : "Delete", Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
            }, rowId);
        }

        // === Shared-with-me row ===
        // Mirrors AddPublicRow but the Apply button uses the shared-apply console cmd
        // (which gates on the SharedWith allowlist instead of IsPublic).
        private void AddSharedRow(CuiElementContainer c, SavedArt art, ulong ownerId, string ownerName, ulong viewerId, int row, float yMin, float yMax)
        {
            string rowId = $"SignArtSaver.Ui.Row.{row}";
            c.Add(new CuiPanel
            {
                Image = { Color = (row % 2 == 0) ? "0.149 0.149 0.149 1" : "0.118 0.118 0.118 1" },
                RectTransform = { AnchorMin = $"0 {yMin}", AnchorMax = $"1 {yMax}" },
            }, UiPanel, rowId);

            string canvasName = !string.IsNullOrEmpty(art.OriginalCanvasName) ? art.OriginalCanvasName : (art.EntityKind ?? "?");
            string canvasDims = art.OriginalImageWidth > 0 && art.OriginalImageHeight > 0
                ? $"  <color=#888>{art.OriginalImageWidth}×{art.OriginalImageHeight}</color>"
                : "";
            string canvasDisplay = EscapeRich(canvasName) + canvasDims;

            // Share-time for the row-sort matches the entry where viewer is the recipient.
            DateTime sharedAt = art.SavedUtc;
            if (art.SharedWith != null)
            {
                var entry = art.SharedWith.FirstOrDefault(s => s.SteamId == viewerId);
                if (entry != null) sharedAt = entry.SharedUtc;
            }

            AddRowLabel(c, rowId, art.Slot.ToString(), "0.02", "0.05", "1 1 1 1");
            AddRowLabel(c, rowId, EscapeRich(art.Name ?? ""), "0.05", "0.22", "1 0.95 0.6 1");
            AddRowLabel(c, rowId, canvasDisplay, "0.22", "0.42", "0.85 0.85 0.85 1");
            AddRowLabel(c, rowId, EscapeRich(ownerName ?? ownerId.ToString()), "0.42", "0.62", "0.6 0.85 1 1");
            AddRowLabel(c, rowId, FormatAge(sharedAt), "0.62", "0.78", "0.85 0.85 0.85 1");

            c.Add(new CuiButton
            {
                Button = { Color = "0.20 0.40 0.55 1", Command = $"signartsaver.ui.preview {ownerId} {art.Slot}" },
                RectTransform = { AnchorMin = "0.69 0.18", AnchorMax = "0.77 0.82" },
                Text = { Text = "View", Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
            }, rowId);

            c.Add(new CuiButton
            {
                Button = { Color = "0.20 0.45 0.20 1", Command = $"signartsaver.ui.apply.shared {ownerId} {art.Slot}" },
                RectTransform = { AnchorMin = "0.78 0.18", AnchorMax = "0.99 0.82" },
                Text = { Text = "Apply to my sign", Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
            }, rowId);
        }

        // === Public Gallery row ===
        private void AddPublicRow(CuiElementContainer c, SavedArt art, ulong ownerId, string ownerName, int row, float yMin, float yMax)
        {
            string rowId = $"SignArtSaver.Ui.Row.{row}";
            c.Add(new CuiPanel
            {
                Image = { Color = (row % 2 == 0) ? "0.149 0.149 0.149 1" : "0.118 0.118 0.118 1" },
                RectTransform = { AnchorMin = $"0 {yMin}", AnchorMax = $"1 {yMax}" },
            }, UiPanel, rowId);

            // Same escaping fix as AddMineRow: escape the user-supplied name, leave the
            // plugin-generated dimension tag intact so its <color=#888> renders.
            string canvasName = !string.IsNullOrEmpty(art.OriginalCanvasName) ? art.OriginalCanvasName : (art.EntityKind ?? "?");
            string canvasDims = art.OriginalImageWidth > 0 && art.OriginalImageHeight > 0
                ? $"  <color=#888>{art.OriginalImageWidth}×{art.OriginalImageHeight}</color>"
                : "";
            string canvasDisplay = EscapeRich(canvasName) + canvasDims;

            AddRowLabel(c, rowId, art.Slot.ToString(), "0.02", "0.05", "1 1 1 1");
            AddRowLabel(c, rowId, EscapeRich(art.Name ?? ""), "0.05", "0.22", "1 0.95 0.6 1");
            AddRowLabel(c, rowId, canvasDisplay, "0.22", "0.42", "0.85 0.85 0.85 1");
            AddRowLabel(c, rowId, EscapeRich(ownerName ?? ownerId.ToString()), "0.42", "0.62", "0.6 0.85 1 1");
            AddRowLabel(c, rowId, FormatAge(art.PublishedUtc ?? art.SavedUtc), "0.62", "0.78", "0.85 0.85 0.85 1");

            c.Add(new CuiButton
            {
                Button = { Color = "0.20 0.40 0.55 1", Command = $"signartsaver.ui.preview {ownerId} {art.Slot}" },
                RectTransform = { AnchorMin = "0.69 0.18", AnchorMax = "0.77 0.82" },
                Text = { Text = "View", Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
            }, rowId);

            c.Add(new CuiButton
            {
                Button = { Color = "0.20 0.45 0.20 1", Command = $"signartsaver.ui.apply.public {ownerId} {art.Slot}" },
                RectTransform = { AnchorMin = "0.78 0.18", AnchorMax = "0.99 0.82" },
                Text = { Text = "Apply to my sign", Align = TextAnchor.MiddleCenter, FontSize = 11, Color = "1 1 1 1" },
            }, rowId);
        }

        private void AddColLabel(CuiElementContainer c, string parent, string text, string xMin, string xMax, bool center)
        {
            c.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = 12, Font = "robotocondensed-bold.ttf",
                         Align = center ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft,
                         Color = "0.85 0.85 0.85 1" },
                RectTransform = { AnchorMin = $"{xMin} 0", AnchorMax = $"{xMax} 1" },
            }, parent);
        }

        private void AddRowLabel(CuiElementContainer c, string parent, string text, string xMin, string xMax, string color)
        {
            c.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = 12, Font = "robotocondensed-regular.ttf",
                         Align = TextAnchor.MiddleLeft, Color = color },
                RectTransform = { AnchorMin = $"{xMin} 0", AnchorMax = $"{xMax} 1" },
            }, parent);
        }

        private void DestroyAllUi(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiImportModal);
            CuiHelper.DestroyUi(player, UiShareModal);
            CuiHelper.DestroyUi(player, UiHelpModal);
            CuiHelper.DestroyUi(player, UiPreview);
            CuiHelper.DestroyUi(player, UiPanel);
            CuiHelper.DestroyUi(player, UiBackdrop);
        }

        // === Import URL modal — single URL input field, applies without saving ===
        // v0.8.3 dropped the dual-field design. Players who want to save the result use
        // Save Sign in the toolbar after the URL paint completes — same bytes, no need to
        // duplicate the save flow inside the import modal.
        private void BuildImportUrlModal(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            CuiHelper.DestroyUi(player, UiImportModal);

            var c = new CuiElementContainer();

            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.85" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true,
            }, "Overlay", UiImportModal);

            string cardId = $"{UiImportModal}.Card";
            c.Add(new CuiPanel
            {
                Image = { Color = "0.082 0.082 0.082 1" },
                RectTransform = { AnchorMin = "0.25 0.32", AnchorMax = "0.75 0.68" },
            }, UiImportModal, cardId);

            // Title bar.
            string titleBarId = $"{UiImportModal}.Title";
            c.Add(new CuiPanel
            {
                Image = { Color = "0.122 0.122 0.122 1" },
                RectTransform = { AnchorMin = "0 0.86", AnchorMax = "1 1" },
            }, cardId, titleBarId);
            c.Add(new CuiLabel
            {
                Text = {
                    Text = "IMPORT URL",
                    FontSize = 16, Font = "robotocondensed-bold.ttf",
                    Align = TextAnchor.MiddleLeft, Color = "1 0.8 0.333 1",
                },
                RectTransform = { AnchorMin = "0.03 0", AnchorMax = "0.92 1" },
            }, titleBarId);
            c.Add(new CuiButton
            {
                Button = { Color = "0.5 0.15 0.15 1", Command = "signartsaver.ui.import.close" },
                RectTransform = { AnchorMin = "0.94 0.15", AnchorMax = "0.99 0.85" },
                Text = { Text = "✕", Align = TextAnchor.MiddleCenter, FontSize = 16, Color = "1 1 1 1" },
            }, titleBarId);

            // Intro text.
            c.Add(new CuiLabel
            {
                Text = {
                    Text = "<color=#cccccc>Paste an image URL below and press <color=#ffcc55>Enter</color> to paint the sign you were aiming at.</color>\n\n" +
                           "<color=#aaaaaa>The URL is not saved to your library. If you want to keep it, click <color=#7ad>Save Sign</color> in the toolbar after the paint completes — that captures the bytes off the sign so the art survives even if the URL dies.</color>",
                    FontSize = 13, Font = "robotocondensed-regular.ttf",
                    Align = TextAnchor.UpperLeft, Color = "1 1 1 1",
                },
                RectTransform = { AnchorMin = "0.04 0.45", AnchorMax = "0.96 0.83" },
            }, cardId);

            // URL input field. Named bg panel as the click target with the input nested
            // inside, mirroring RaidWindow's TZ search pattern.
            string urlBgId = $"{UiImportModal}.UrlBg";
            c.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.14 0.18 1" },
                RectTransform = { AnchorMin = "0.04 0.30", AnchorMax = "0.96 0.42" },
            }, cardId, urlBgId);
            c.Add(new CuiElement
            {
                Name = $"{UiImportModal}.UrlInput",
                Parent = urlBgId,
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = ImportPlaceholder,
                        FontSize = 14, CharsLimit = 512,
                        Align = TextAnchor.MiddleLeft, Color = "0.5 0.5 0.5 1",
                        Command = "signartsaver.ui.import.apply",
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.02 0", AnchorMax = "0.98 1" },
                },
            });

            // Cancel button.
            c.Add(new CuiButton
            {
                Button = { Color = "0.30 0.30 0.30 1", Command = "signartsaver.ui.import.close" },
                RectTransform = { AnchorMin = "0.42 0.08", AnchorMax = "0.58 0.20" },
                Text = { Text = "Cancel", Align = TextAnchor.MiddleCenter, FontSize = 13, Color = "1 1 1 1" },
            }, cardId);

            CuiHelper.AddUi(player, c);
        }

        // === Help modal — full feature reference, no chat needed ===
        private void BuildHelpModal(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            CuiHelper.DestroyUi(player, UiHelpModal);

            var c = new CuiElementContainer();

            // Backdrop.
            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.78" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true,
            }, "Overlay", UiHelpModal);

            // Card.
            string cardId = $"{UiHelpModal}.Card";
            c.Add(new CuiPanel
            {
                Image = { Color = "0.082 0.082 0.082 0.98" },
                RectTransform = { AnchorMin = "0.22 0.10", AnchorMax = "0.78 0.92" },
            }, UiHelpModal, cardId);

            // Title bar.
            string titleBarId = $"{UiHelpModal}.Title";
            c.Add(new CuiPanel
            {
                Image = { Color = "0.122 0.122 0.122 1" },
                RectTransform = { AnchorMin = "0 0.93", AnchorMax = "1 1" },
            }, cardId, titleBarId);
            c.Add(new CuiLabel
            {
                Text = {
                    Text = "SIGN ART SAVER — QUICK HELP",
                    FontSize = 16, Font = "robotocondensed-bold.ttf",
                    Align = TextAnchor.MiddleLeft, Color = "1 0.8 0.333 1",
                },
                RectTransform = { AnchorMin = "0.03 0", AnchorMax = "0.92 1" },
            }, titleBarId);
            c.Add(new CuiButton
            {
                Button = { Color = "0.5 0.15 0.15 1", Command = "signartsaver.ui.help.close" },
                RectTransform = { AnchorMin = "0.94 0.15", AnchorMax = "0.99 0.85" },
                Text = { Text = "✕", Align = TextAnchor.MiddleCenter, FontSize = 16, Color = "1 1 1 1" },
            }, titleBarId);

            // Body — a single block of rich text. Headings in yellow, button names in cyan.
            string body =
                "<size=14><color=#ffff66>Saving art</color></size>\n" +
                "  • Aim at a painted sign, click <color=#7ad>Save Sign</color> in the toolbar — captures bytes off the sign immediately.\n" +
                "  • Or run <color=#aaaaaa>/sil [url]</color> via Sign Artist — auto-captures both URL + bytes.\n" +
                "  • Works on vanilla painted signs AND Sign Artist URL-painted signs.\n\n" +
                "<size=14><color=#ffff66>Importing from a URL</color></size>\n" +
                "  • Aim at a sign, click <color=#7ad>Import URL</color> in the toolbar.\n" +
                "  • Paste the URL, press Enter — the sign repaints with the image.\n" +
                "  • To keep it, click <color=#7ad>Save Sign</color> after the paint completes — captures the bytes off the painted sign so the art survives even if the URL dies.\n\n" +
                "<size=14><color=#ffff66>Applying art</color></size>\n" +
                "  • In <color=#7ad>My Library</color> or <color=#7ad>Public Gallery</color> tabs, click <color=#7ad>View</color> on any row to preview at native size.\n" +
                "  • Click <color=#7ad>Apply to my sign</color>, then look at YOUR sign and press USE.\n" +
                "  • Auto-resizes to the target canvas — saved 256×128 art applies cleanly to a 512×512 frame.\n\n" +
                "<size=14><color=#ffff66>Managing your library</color></size>\n" +
                "  • <color=#7ad>Rename</color>: edit the name inline, press Enter.\n" +
                "  • <color=#7ad>Delete</color>: two clicks within 5s.\n" +
                "  • <color=#7ad>Wipe library</color>: red toolbar button, two clicks within 10s. Removes ALL slots and their PNG files.\n\n" +
                "<size=14><color=#ffff66>Sharing publicly</color></size>\n" +
                "  • Click <color=#7ad>Private</color> on any of your slots → flips to <color=#5f5>Public</color>. Slot now appears in the Public Gallery for everyone.\n" +
                "  • Public Gallery has a search box: filters by artist name, slot name, or canvas type.\n" +
                "  • Anyone can apply public art to their <color=#ffcc55>own</color> sign — your art, their canvas.\n\n" +
                "<size=14><color=#ffff66>Where your art lives</color></size>\n" +
                "  Server-side at <color=#aaaaaa>oxide/data/SignArtSaver/images/[steamid]/[slot].png</color> — survives wipes.";

            c.Add(new CuiLabel
            {
                Text = {
                    Text = body,
                    FontSize = 12, Font = "robotocondensed-regular.ttf",
                    Align = TextAnchor.UpperLeft, Color = "1 1 1 1",
                },
                RectTransform = { AnchorMin = "0.04 0.10", AnchorMax = "0.96 0.91" },
            }, cardId);

            c.Add(new CuiButton
            {
                Button = { Color = "0.30 0.30 0.30 1", Command = "signartsaver.ui.help.close" },
                RectTransform = { AnchorMin = "0.40 0.02", AnchorMax = "0.60 0.08" },
                Text = { Text = "Close", Align = TextAnchor.MiddleCenter, FontSize = 13, Color = "1 1 1 1" },
            }, cardId);

            CuiHelper.AddUi(player, c);
        }

        // === Preview modal (image viewer) ===

        // Open a fullscreen-overlay modal showing the saved image at its NATIVE canvas
        // pixel dimensions (1px source = 1px screen at a 1920×1080 reference resolution).
        // Cap at 70% of either screen axis so an XXL Picture Frame doesn't dominate;
        // floor at 10% so a Carvable Pumpkin (128×128) is still readable. Title is fixed
        // at top, info + buttons fixed at bottom — image floats centered between.
        private void BuildPreviewModal(BasePlayer player, SavedArt art, ulong ownerId, string ownerName, uint crc)
        {
            var c = new CuiElementContainer();

            // Backdrop covers the whole screen — modal pattern, blocks accidental clicks
            // through to the world.
            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.78" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true,
            }, "Overlay", UiPreview);

            // === Image rect at native canvas pixel dimensions ===
            // Reference: 1920×1080. 1px source = 1px screen at 1080p — so a 256×128 small
            // wooden sign renders at 256×128 actual pixels, a 512×512 XL frame at 512×512.
            // No artificial floor: small canvases stay small (matches "actual size" intent).
            // Cap at 80% per axis just to ensure the largest XXL Picture Frame
            // (1024×512) still leaves room for the title + info + buttons.
            const float refW = 1920f;
            const float refH = 1080f;
            const float maxFracW = 0.80f;
            const float maxFracH = 0.80f;

            int srcW = art.OriginalImageWidth  > 0 ? art.OriginalImageWidth  : 256;
            int srcH = art.OriginalImageHeight > 0 ? art.OriginalImageHeight : 128;
            float wFrac = srcW / refW;
            float hFrac = srcH / refH;

            // Cap (uniform scale-down preserving aspect). No floor.
            float overShoot = Math.Max(wFrac / maxFracW, hFrac / maxFracH);
            if (overShoot > 1f) { wFrac /= overShoot; hFrac /= overShoot; }

            // Centered horizontally, biased slightly above vertical center so info+buttons
            // below the image have predictable space.
            const float imgYCenter = 0.55f;
            float imgYTop    = imgYCenter + hFrac * 0.5f;
            float imgYBottom = imgYCenter - hFrac * 0.5f;
            float imgXMin    = 0.50f - wFrac * 0.5f;
            float imgXMax    = 0.50f + wFrac * 0.5f;

            // Subtle outline behind the image so transparency / dark images stay visible.
            c.Add(new CuiPanel
            {
                Image = { Color = "0.20 0.20 0.20 1" },
                RectTransform = {
                    AnchorMin = $"{imgXMin - 0.003f} {imgYBottom - 0.005f}",
                    AnchorMax = $"{imgXMax + 0.003f} {imgYTop + 0.005f}",
                },
            }, UiPreview);

            // The image itself — referenced by FileStorage CRC.
            c.Add(new CuiElement
            {
                Name = UiPreviewImg,
                Parent = UiPreview,
                Components =
                {
                    new CuiRawImageComponent { Png = crc.ToString(), Color = "1 1 1 1" },
                    new CuiRectTransformComponent {
                        AnchorMin = $"{imgXMin} {imgYBottom}",
                        AnchorMax = $"{imgXMax} {imgYTop}",
                    },
                },
            });

            // === Title bar at top of screen ===
            c.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.10 0.10 0.95" },
                RectTransform = { AnchorMin = "0.18 0.92", AnchorMax = "0.82 0.98" },
            }, UiPreview, $"{UiPreview}.TitleBar");
            c.Add(new CuiLabel
            {
                Text = {
                    Text = $"Slot {art.Slot}: {EscapeRich(art.Name ?? "")}",
                    FontSize = 15, Font = "robotocondensed-bold.ttf",
                    Align = TextAnchor.MiddleLeft, Color = "1 0.8 0.333 1",
                },
                RectTransform = { AnchorMin = "0.03 0", AnchorMax = "0.92 1" },
            }, $"{UiPreview}.TitleBar");
            c.Add(new CuiButton
            {
                Button = { Color = "0.5 0.15 0.15 1", Command = "signartsaver.ui.preview.close" },
                RectTransform = { AnchorMin = "0.94 0.15", AnchorMax = "0.99 0.85" },
                Text = { Text = "✕", Align = TextAnchor.MiddleCenter, FontSize = 16, Color = "1 1 1 1" },
            }, $"{UiPreview}.TitleBar");

            // === Info bar — fixed at bottom of screen, above buttons ===
            string canvasText = !string.IsNullOrEmpty(art.OriginalCanvasName) ? art.OriginalCanvasName : (art.EntityKind ?? "?");
            string dimsText = art.OriginalImageWidth > 0 && art.OriginalImageHeight > 0
                ? $"{art.OriginalImageWidth}×{art.OriginalImageHeight} px"
                : "unknown size";
            string artistLine = ownerId != (ulong)player.userID ? $"  ·  by <color=#7ad>{EscapeRich(ownerName)}</color>" : "";

            c.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.10 0.10 0.95" },
                RectTransform = { AnchorMin = "0.20 0.13", AnchorMax = "0.80 0.18" },
            }, UiPreview, $"{UiPreview}.InfoBar");
            c.Add(new CuiLabel
            {
                Text = {
                    Text = $"<color=#ffff66>{EscapeRich(canvasText)}</color>  ·  <color=#cccccc>{dimsText}</color>  ·  <color=#888>{FormatAge(art.SavedUtc)}</color>{artistLine}",
                    FontSize = 12, Font = "robotocondensed-regular.ttf",
                    Align = TextAnchor.MiddleCenter, Color = "1 1 1 1",
                },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.98 1" },
            }, $"{UiPreview}.InfoBar");

            // === Action buttons — bottom strip ===
            c.Add(new CuiButton
            {
                Button = { Color = "0.20 0.45 0.20 1", Command = "signartsaver.ui.preview.apply" },
                RectTransform = { AnchorMin = "0.30 0.05", AnchorMax = "0.50 0.11" },
                Text = { Text = "Apply to my sign", Align = TextAnchor.MiddleCenter, FontSize = 13, Color = "1 1 1 1" },
            }, UiPreview);
            c.Add(new CuiButton
            {
                Button = { Color = "0.30 0.30 0.30 1", Command = "signartsaver.ui.preview.close" },
                RectTransform = { AnchorMin = "0.52 0.05", AnchorMax = "0.70 0.11" },
                Text = { Text = "Close", Align = TextAnchor.MiddleCenter, FontSize = 13, Color = "1 1 1 1" },
            }, UiPreview);

            CuiHelper.DestroyUi(player, UiPreview);
            CuiHelper.AddUi(player, c);
        }

        // Tear down preview state: CUI element + FileStorage entry. Called from explicit
        // close, from Unload, from OnPlayerDisconnected, and from main-panel close.
        private void ClosePreview(BasePlayer player, BrowsePanel panel, bool refreshPanel)
        {
            if (player != null && player.IsConnected) CuiHelper.DestroyUi(player, UiPreview);
            if (panel == null) return;
            if (panel.PreviewCrc != 0)
            {
                try { FileStorage.server.Remove(panel.PreviewCrc, FileStorage.Type.png, new NetworkableId(0)); }
                catch (Exception e) { PrintWarning($"FileStorage.Remove for preview crc {panel.PreviewCrc} failed: {e.Message}"); }
                panel.PreviewCrc = 0;
            }
            panel.PreviewOwnerId = 0;
            panel.PreviewSlot = 0;
            if (refreshPanel && player != null && player.IsConnected) RefreshBrowsePanel(player);
        }

        #endregion

        #region Helpers

        private bool HasUse(BasePlayer player) =>
            player != null && (permission.UserHasPermission(player.UserIDString, PermUse) || player.IsAdmin);

        // Resolve a steamid → display name. Priority: cached OwnerName on the library →
        // Oxide/covalence player records (works for offline players too, names are kept by
        // Oxide's permission system) → online BasePlayer.displayName → steamid string fallback.
        private string ResolveOwnerName(ulong ownerId, PlayerLibrary lib = null)
        {
            if (lib != null && !string.IsNullOrEmpty(lib.OwnerName)) return lib.OwnerName;
            try
            {
                var iplayer = covalence.Players.FindPlayerById(ownerId.ToString());
                if (iplayer != null && !string.IsNullOrEmpty(iplayer.Name))
                {
                    // Cache it back if we have a library for this id.
                    if (lib != null)
                    {
                        lib.OwnerName = iplayer.Name;
                        StampLib(lib);
                    }
                    return iplayer.Name;
                }
            }
            catch { /* fall through to id */ }
            var bp = BasePlayer.FindByID(ownerId);
            if (bp != null && !string.IsNullOrEmpty(bp.displayName))
            {
                if (lib != null) lib.OwnerName = bp.displayName;
                return bp.displayName;
            }
            return ownerId.ToString();
        }

        private bool HasAdmin(BasePlayer player) =>
            player != null && (permission.UserHasPermission(player.UserIDString, PermAdmin) || player.IsAdmin);

        // Order matters: Rust's class hierarchy currently makes Signage / PhotoFrame /
        // CarvablePumpkin / PaintedItemStorageEntity all siblings (under BaseEntity), so any
        // order works today. But we check most-derived FIRST as defense against a future
        // Rust patch that promotes one of them to a base class — without this guard, a
        // PhotoFrame would silently route through the Signage branch and write to the wrong
        // texture field. Subclasses we already handle correctly via "is" matching:
        //   OrnateFrame, FlagTogglePhotoFrame  → PhotoFrame branch (extend PhotoFrame)
        //   SpinnerWheel, ReactiveTarget,
        //   PaintableReactiveTarget            → Signage branch (extend Signage)
        private string ResolveKind(BaseEntity entity)
        {
            if (entity is PaintedItemStorageEntity) return KindPaintedItem;
            if (entity is PhotoFrame) return KindPhotoFrame;
            if (entity is CarvablePumpkin) return KindPumpkin;
            if (entity is Signage) return KindSign;
            return null;
        }

        private bool TryRaycastSign(BasePlayer player, out BaseEntity entity, out string kind, out uint textureIndex)
        {
            entity = null;
            kind = null;
            textureIndex = 0;
            if (player == null || player.eyes == null) return false;
            var ray = player.eyes.HeadRay();
            // Match Sign Artist's raycast parameters (Cmd.SilCommand → ApplySignAndStore) —
            // DefaultRaycastLayers + QueryTriggerInteraction.Ignore — so we hit the same
            // entities Sign Artist does. Earlier 0-arg version was using AllLayers + global
            // trigger handling which on some Rust patches misses picture-frame colliders.
            float range = config?.AimRangeMeters ?? 8f;
            if (!Physics.Raycast(ray, out var hit, range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
            // hit.GetEntity() walks up the parent chain via Rust's extension helper. As a
            // fallback for cases where the immediate hit transform doesn't expose a
            // BaseEntity (e.g., a child collider on a picture frame), traverse up.
            var ent = hit.GetEntity() ?? hit.collider?.GetComponentInParent<BaseEntity>();
            if (ent == null) return false;
            kind = ResolveKind(ent);
            if (kind == null) return false;
            entity = ent;
            return true;
        }

        // Normalize URL for dedupe-hash. Strips the query string when StripQueryForDedupe is on
        // so Discord CDN signed-URL rotation doesn't create duplicate slots.
        private string NormalizeUrlForHash(string url)
        {
            if (string.IsNullOrEmpty(url)) return url ?? "";
            if (!config.StripQueryForDedupe) return url;
            try
            {
                var u = new Uri(url);
                return u.GetLeftPart(UriPartial.Path);
            }
            catch
            {
                int q = url.IndexOf('?');
                return q > 0 ? url.Substring(0, q) : url;
            }
        }

        private string UrlHashKey(string url)
        {
            var n = NormalizeUrlForHash(url);
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(n));
                var sb = new StringBuilder();
                for (int i = 0; i < 5 && i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private bool UrlIsBlocked(string url)
        {
            if (string.IsNullOrEmpty(url) || config.UrlBlocklist == null) return false;
            string lower = url.ToLowerInvariant();
            foreach (var s in config.UrlBlocklist)
            {
                if (string.IsNullOrEmpty(s)) continue;
                if (lower.Contains(s.ToLowerInvariant())) return true;
            }
            return false;
        }

        private bool IsDiscordCdn(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            try
            {
                var host = new Uri(url).Host.ToLowerInvariant();
                return host.EndsWith("discordapp.com") || host.EndsWith("discordapp.net") ||
                       host.EndsWith("discord.com")    || host.EndsWith("discord.media");
            }
            catch { return false; }
        }

        private string FormatAge(DateTime savedUtc)
        {
            var ts = DateTime.UtcNow - savedUtc;
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d ago";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h ago";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m ago";
            return "just now";
        }

        private string Truncate(string s, int len)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= len) return s ?? "";
            return s.Substring(0, Math.Max(0, len - 1)) + "…";
        }

        // CUI labels render rich-text by default; user-supplied names/URLs need angle-bracket
        // escaping so a hostile name like "<color=#000>HIDDEN</color>" can't break layout.
        private string EscapeRich(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("<", "‹").Replace(">", "›");

        // Slot names enter chat messages via `"…{art.Name}…"` patterns in many places;
        // every site escaping individually is error-prone. Sanitize at the write source
        // instead: strip control chars (newline / carriage return / tab — would break
        // chat layout), and replace `<`/`>` with their look-alike Unicode angle-quote
        // siblings so rich-text tags can't be injected. Idempotent on already-clean input.
        private string SanitizeSlotName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c == '<') sb.Append('‹');
                else if (c == '>') sb.Append('›');
                else if (c < ' ') continue;
                else sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        // Stricter URL re-vet at apply time for cross-library applies (public gallery /
        // shared-with-me). Even when the substring blocklist passes, this rejects IP
        // literals, loopback hostnames, and the obvious metadata-service / mDNS suffixes
        // so an artist can't trick a buyer's server into SSRF'ing internal endpoints if
        // the slot's bytes file ever goes missing and the URL fallback path engages.
        // Permissive on scheme (http + https both accepted; many durable hosts still use
        // http) — the network defense is the host check, not the scheme.
        private bool IsUrlSafeForApply(string url, out string err)
        {
            err = null;
            if (string.IsNullOrEmpty(url)) { err = "empty"; return false; }
            Uri u;
            try { u = new Uri(url); }
            catch (Exception e) { err = $"unparseable ({e.Message})"; return false; }
            if (u.Scheme != "https" && u.Scheme != "http") { err = $"scheme '{u.Scheme}'"; return false; }
            string host = u.Host?.ToLowerInvariant() ?? "";
            if (host.Length == 0) { err = "no host"; return false; }
            if (System.Net.IPAddress.TryParse(host, out _)) { err = "IP literal"; return false; }
            if (host == "localhost" || host.EndsWith(".localhost") ||
                host.EndsWith(".local") || host.EndsWith(".internal") ||
                host.EndsWith(".lan") || host.EndsWith(".home"))
            {
                err = "private hostname"; return false;
            }
            if (UrlIsBlocked(url)) { err = "blocklist"; return false; }
            return true;
        }

        private string Tag() => "<color=#7ad>[Save Art]</color> ";
        private string Err(string s) => $"<color=#cd412b>{s}</color>";
        private string Warn(string s) => $"<color=#ffcc55>{s}</color>";
        private string Ok(string s) => $"<color=#55ff55>{s}</color>";

        #endregion

        #region Byte mode (FileStorage I/O)

        // True if the entity has art applied to ANY of its textures. Used by /saveart debug
        // to report whether something's painted regardless of which texture index. Picture
        // frames, banners, and double-sided hanging signs can have art on any of their
        // texture slots; checking only index 0 misses the back side of a banner, the
        // unpainted face of a hanging sign, or a PhotoFrame whose overlay CRC lives in a
        // different field than GetTextureCRCs() exposes on some patch versions.
        private bool EntityHasAnyArt(BaseEntity entity)
        {
            if (entity == null) return false;
            if (entity is Signage sign)
            {
                var crcs = sign.GetTextureCRCs();
                if (crcs == null) return false;
                for (int i = 0; i < crcs.Length; i++) if (crcs[i] != 0) return true;
                return false;
            }
            if (entity is PhotoFrame frame)
            {
                var crcs = frame.GetTextureCRCs();
                if (crcs != null) for (int i = 0; i < crcs.Length; i++) if (crcs[i] != 0) return true;
                // Fallback: Sign Artist writes the overlay CRC directly to this field;
                // some Rust patch versions don't surface it via GetTextureCRCs().
                if (frame._overlayTextureCrc != 0) return true;
                return false;
            }
            if (entity is CarvablePumpkin pumpkin)
            {
                var crcs = pumpkin.textureIDs;
                if (crcs == null) return false;
                for (int i = 0; i < crcs.Length; i++) if (crcs[i] != 0) return true;
                return false;
            }
            return false;
        }

        // Read the CRC32 currently set on the entity for the given texture index. Returns
        // false if the entity has no painted texture there. Mirrors Sign Artist's
        // PaintableSignage / PaintableFrame / PaintablePumpkin TextureIds accessors.
        private bool TryGetEntityCrc(BaseEntity entity, uint textureIndex, out uint crc)
        {
            crc = 0;
            if (entity is Signage sign)
            {
                var crcs = sign.GetTextureCRCs();
                if (crcs == null || textureIndex >= crcs.Length) return false;
                crc = crcs[textureIndex];
            }
            else if (entity is PhotoFrame frame)
            {
                // PhotoFrame is single-texture; ignore textureIndex.
                var crcs = frame.GetTextureCRCs();
                if (crcs == null || crcs.Length == 0) return false;
                crc = crcs[0];
            }
            else if (entity is CarvablePumpkin pumpkin)
            {
                var crcs = pumpkin.textureIDs;
                if (crcs == null || textureIndex >= crcs.Length) return false;
                crc = crcs[textureIndex];
            }
            else return false;
            return crc != 0;
        }

        // Pull the raw PNG bytes for an entity's texture out of FileStorage. Returns null
        // if no image is present (CRC == 0) or if FileStorage has no record.
        private byte[] FetchPngFromEntity(BaseEntity entity, uint textureIndex)
        {
            if (entity == null || entity.net == null) return null;
            if (!TryGetEntityCrc(entity, textureIndex, out var crc)) return null;
            try
            {
                return FileStorage.server.Get(crc, FileStorage.Type.png, entity.net.ID);
            }
            catch (Exception e)
            {
                PrintWarning($"FileStorage.Get failed for crc={crc} netid={entity.net.ID}: {e.Message}");
                return null;
            }
        }

        // Write PNG bytes to the entity's FileStorage and bind the new CRC to the entity's
        // texture slot. Mirrors Sign Artist's apply path (lines 529-536) including the
        // remove-old-CRC-first dance so FileStorage doesn't accumulate orphans.
        private bool ApplyBytesToEntity(BaseEntity entity, string targetKind, uint textureIndex, byte[] bytes, out string error)
        {
            error = null;
            if (entity == null || entity.net == null) { error = "entity has no net id"; return false; }
            if (bytes == null || bytes.Length == 0) { error = "empty bytes"; return false; }

            try
            {
                uint newCrc;
                if (targetKind == KindSign && entity is Signage sign)
                {
                    sign.EnsureInitialized();
                    if (sign.textureIDs == null) { error = "sign.textureIDs null after EnsureInitialized"; return false; }
                    if (textureIndex >= sign.textureIDs.Length) { error = $"textureIndex {textureIndex} out of range (size {sign.textureIDs.Length})"; return false; }
                    var existing = sign.textureIDs[textureIndex];
                    newCrc = FileStorage.server.Store(bytes, FileStorage.Type.png, entity.net.ID, textureIndex);
                    if (newCrc == 0) { error = "FileStorage rejected the bytes"; return false; }
                    if (existing != 0 && existing != newCrc)
                        FileStorage.server.Remove(existing, FileStorage.Type.png, entity.net.ID);
                    sign.textureIDs[textureIndex] = newCrc;
                    sign.SendNetworkUpdate();
                }
                else if (targetKind == KindPhotoFrame && entity is PhotoFrame frame)
                {
                    var existing = frame._overlayTextureCrc;
                    newCrc = FileStorage.server.Store(bytes, FileStorage.Type.png, entity.net.ID);
                    if (newCrc == 0) { error = "FileStorage rejected the bytes"; return false; }
                    if (existing != 0 && existing != newCrc)
                        FileStorage.server.Remove(existing, FileStorage.Type.png, entity.net.ID);
                    frame._overlayTextureCrc = newCrc;
                    frame.SendNetworkUpdate();
                }
                else if (targetKind == KindPumpkin && entity is CarvablePumpkin pumpkin)
                {
                    int size = Mathf.Max(pumpkin.paintableSources?.Length ?? 1, 1);
                    if (pumpkin.textureIDs == null || pumpkin.textureIDs.Length != size)
                        Array.Resize(ref pumpkin.textureIDs, size);
                    if (textureIndex >= pumpkin.textureIDs.Length) { error = $"textureIndex {textureIndex} out of range (size {pumpkin.textureIDs.Length})"; return false; }
                    var existing = pumpkin.textureIDs[textureIndex];
                    newCrc = FileStorage.server.Store(bytes, FileStorage.Type.png, entity.net.ID, textureIndex);
                    if (newCrc == 0) { error = "FileStorage rejected the bytes"; return false; }
                    if (existing != 0 && existing != newCrc)
                        FileStorage.server.Remove(existing, FileStorage.Type.png, entity.net.ID);
                    pumpkin.textureIDs[textureIndex] = newCrc;
                    pumpkin.SendNetworkUpdate();
                }
                else if (targetKind == KindPaintedItem && entity is PaintedItemStorageEntity painted)
                {
                    // PaintedItemStorageEntity (drawable windows, paintable reactive target,
                    // others) keeps its CRC in private _currentImageCrc. Sign Artist v1.4.6
                    // doesn't have a wrapper class for this entity at all — SignArtSaver is the
                    // only path. We mirror the engine's Server_UpdateImage RPC: Remove old,
                    // Store new, set field, SendNetworkUpdate. textureIndex is ignored (single
                    // CRC only, like PhotoFrame).
                    if (_paintedItemCrcField == null)
                    {
                        error = "PaintedItemStorageEntity._currentImageCrc reflection failed (Rust patch may have renamed the field)";
                        return false;
                    }
                    uint existing = 0u;
                    try { existing = (uint)_paintedItemCrcField.GetValue(painted); }
                    catch (Exception e) { error = $"reflection get failed: {e.Message}"; return false; }
                    newCrc = FileStorage.server.Store(bytes, FileStorage.Type.png, entity.net.ID);
                    if (newCrc == 0) { error = "FileStorage rejected the bytes"; return false; }
                    if (existing != 0 && existing != newCrc)
                        FileStorage.server.Remove(existing, FileStorage.Type.png, entity.net.ID);
                    try { _paintedItemCrcField.SetValue(painted, newCrc); }
                    catch (Exception e) { error = $"reflection set failed: {e.Message}"; return false; }
                    painted.SendNetworkUpdate();
                }
                else { error = $"unsupported entity kind '{targetKind}' for entity {entity.GetType().Name}"; return false; }
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        // Filesystem layout:
        //   <oxide.data>/SignArtSaver/players.json
        //   <oxide.data>/SignArtSaver/images/<steamid>/<slot>.png
        // BytesPath in JSON stores the relative-to-SignArtSaver path so apply works
        // regardless of which user owns the slot (admin cross-player apply).
        private string ImageRelativePath(ulong userId, int slot) => $"images/{userId}/{slot}.png";

        private string ImageFullPath(ulong userId, int slot) =>
            Path.Combine(Interface.Oxide.DataDirectory, "SignArtSaver", "images", userId.ToString(), $"{slot}.png");

        private bool TryWritePngFile(ulong userId, int slot, byte[] bytes, out string error)
        {
            error = null;
            try
            {
                var path = ImageFullPath(userId, slot);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        private void DeleteSlotPng(SavedArt art)
        {
            if (art == null || string.IsNullOrEmpty(art.BytesPath)) return;
            try
            {
                var path = Path.Combine(Interface.Oxide.DataDirectory, "SignArtSaver", art.BytesPath);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) { PrintWarning($"DeleteSlotPng failed for {art.BytesPath}: {e.Message}"); }
        }

        private void DeleteUserImagesDir(ulong userId)
        {
            try
            {
                var dir = Path.Combine(Interface.Oxide.DataDirectory, "SignArtSaver", "images", userId.ToString());
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception e) { PrintWarning($"DeleteUserImagesDir failed for {userId}: {e.Message}"); }
        }

        private string ByteSha1(byte[] bytes)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(bytes);
                var sb = new StringBuilder();
                for (int i = 0; i < 5 && i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // Look up the canvas pixel dimensions for the given short prefab. Returns null if
        // the prefab isn't in our table (e.g. a future Rust update introduces a new sign).
        private (int W, int H)? LookupCanvasSize(string shortPrefab)
        {
            if (string.IsNullOrEmpty(shortPrefab)) return null;
            return CanvasInfo.TryGetValue(shortPrefab, out var info) ? (info.W, info.H) : ((int, int)?)null;
        }

        // Best-effort friendly name for a prefab not in CanvasInfo. "sign.huge.banner.bonus"
        // → "Sign Huge Banner Bonus". Only used as a fallback for OriginalCanvasName when the
        // short prefab isn't known to the lookup table.
        private string PrettifyShortPrefab(string shortPrefab)
        {
            if (string.IsNullOrEmpty(shortPrefab)) return null;
            var parts = shortPrefab.Split('.');
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                if (parts[i].Length == 0) continue;
                sb.Append(char.ToUpperInvariant(parts[i][0]));
                if (parts[i].Length > 1) sb.Append(parts[i].Substring(1));
            }
            return sb.ToString();
        }

        // Pull width + height out of a PNG IHDR chunk without decoding the full image.
        // Header layout: 8-byte signature, then IHDR chunk: 4-byte length, 'IHDR' (4),
        // 4-byte width (BE), 4-byte height (BE). Width is at bytes 16-19; height at 20-23.
        // 24-byte input is enough.
        private bool TryReadPngDimensions(byte[] bytes, out int width, out int height)
        {
            width = height = 0;
            if (bytes == null || bytes.Length < 24) return false;
            // PNG signature: 89 50 4E 47 0D 0A 1A 0A
            if (bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47) return false;
            width  = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return width > 0 && height > 0 && width < 8192 && height < 8192; // sanity
        }

        // Find a CanvasInfo entry matching (w, h). For ambiguous dimensions (e.g. 512×256
        // matches both Medium Wooden Sign and Large Wooden Sign) returns the first dict
        // entry — best-effort; the user gets a reasonable family name plus accurate dims.
        private bool TryMatchCanvasByDimensions(int w, int h, out string shortPrefab, out string name)
        {
            foreach (var kv in CanvasInfo)
            {
                if (kv.Value.W == w && kv.Value.H == h)
                {
                    shortPrefab = kv.Key;
                    name = kv.Value.Name;
                    return true;
                }
            }
            shortPrefab = null;
            name = null;
            return false;
        }

        // Resize PNG bytes to (targetWidth × targetHeight) using HighQualityBicubic
        // interpolation. No-op (returns the input array) if the source already matches.
        // Mirrors Sign Artist's Extensions.ResizeImage (lines 1981-2037) with PNG-only
        // output and no timestamp watermark.
        private bool TryResizePngBytes(byte[] sourceBytes, int targetWidth, int targetHeight, out byte[] result, out string error)
        {
            result = sourceBytes;
            error = null;
            // Decompression-bomb guard: parse the IHDR header and refuse anything
            // whose decoded RGBA size would exceed ~64 MiB. A flat-color 30000x30000
            // PNG compresses well under 4 MiB but decodes to ~3.6 GB — would OOM-kill
            // the server. TryReadPngDimensions also enforces 8192/side via its own
            // sanity bound; the pixel-budget check below is the belt-and-suspenders.
            if (TryReadPngDimensions(sourceBytes, out int srcW, out int srcH))
            {
                const long MaxDecodedPixels = 64L * 1024 * 1024 / 4; // 64 MiB / 4 bytes per RGBA
                if ((long)srcW * srcH > MaxDecodedPixels)
                {
                    error = $"PNG dimensions {srcW}x{srcH} exceed decode budget";
                    return false;
                }
            }
            else
            {
                error = "PNG header unreadable or dimensions out of sanity range (8192/side)";
                return false;
            }
            try
            {
                using (var src = new MemoryStream(sourceBytes))
                using (var srcBmp = new SDBitmap(src))
                {
                    if (srcBmp.Width == targetWidth && srcBmp.Height == targetHeight)
                        return true; // no-op
                    using (var dstBmp = new SDBitmap(targetWidth, targetHeight))
                    using (var g = SDGraphics.FromImage(dstBmp))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(srcBmp, new SDRectangle(0, 0, targetWidth, targetHeight));
                        using (var dst = new MemoryStream())
                        {
                            dstBmp.Save(dst, ImageFormat.Png);
                            result = dst.ToArray();
                            return true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                result = sourceBytes; // fall back to original on failure
                return false;
            }
        }

        #endregion
    }
}
