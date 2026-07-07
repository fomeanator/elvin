using System;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The drop-in app bootstrap — the whole Liminal-style flow in one component:
    /// fetch the manifest from a server, boot-prefetch its assets, raise the
    /// <see cref="NovelShell"/> (boot → carousel → name → loading → title), and on
    /// Play stream the chosen chapter's <c>.lvn</c> and run it through a wired
    /// <see cref="VnStage"/>, updating the HUD, then loop back to the carousel.
    ///
    /// <para>Scene setup: one GameObject with this component (set
    /// <see cref="ServerUrl"/> + <see cref="ShellTheme"/>) and a second GameObject
    /// with a <see cref="VnStage"/> (its own UIDocument, a lower panel
    /// <c>sortingOrder</c> than the shell) assigned to <see cref="Stage"/>.</para>
    /// </summary>
    public sealed class NovelApp : MonoBehaviour
    {
        /// <summary>Shell lifecycle for the embedding game: a chapter is about
        /// to play (analytics, music ducking, achievements). Args: title, chapter.</summary>
        public event System.Action<LvnTitle, LvnChapter> ChapterStarted;

        /// <summary>A chapter finished END-TO-END (not an exit-to-menu).</summary>
        public event System.Action<LvnTitle, LvnChapter> ChapterFinished;

        [Tooltip("Content origin — the LVN server (manifest + scripts + assets).")]
        public string ServerUrl = "http://127.0.0.1:8000";

        [Tooltip("Offline build: load the novel from content bundled in StreamingAssets " +
                 "instead of a server. The exporter writes the manifest, scripts and assets " +
                 "under StreamingAssets/<BundleSubdir>, mirroring the server's URL paths.")]
        public bool OfflineBundled = false;

        [Tooltip("Subfolder under StreamingAssets that holds the bundled content (offline builds).")]
        public string BundleSubdir = "lvn";

        [Tooltip("The VnStage that renders chapters. Its panel sortingOrder should be below the shell's (30).")]
        public VnStage Stage;

        [Tooltip("Language code for localized chapters. When set, each chapter loads " +
                 "its sidecar string catalog <script>.<locale>.json; lines with a " +
                 "text_id resolve through it. Empty = chapters use their inline text.")]
        public string Locale = "";

        [Tooltip("Runtime ThemeStyleSheet so the shell's text has a font.")]
        public ThemeStyleSheet ShellTheme;

        [Tooltip("Optional: Resources path to a ThemeStyleSheet, loaded when ShellTheme is unset. " +
                 "Lets you wire the theme by string (e.g. \"UI/AppLoading/UnityDefaultRuntimeTheme\").")]
        public string ThemeResourcePath = "";

        public bool AskName = true;

        [Tooltip("Player/account id for server-synced saves (/v1/state?user=…). Leave " +
                 "empty to use a per-device id generated once and kept in PlayerPrefs. " +
                 "Stats always work offline; the server is a durable cross-device backup.")]
        public string UserId = "";

        [Tooltip("Shared secret gating this user's server saves (X-State-Key). MUST be the same on every device when UserId is a cross-device account; leave empty for a per-device secret.")]
        public string StateKey = "";

        [Tooltip("Live content sync: poll the server's version endpoint this often (seconds). " +
                 "Edit a .lvn or the manifest on the server and the app reloads within one interval. " +
                 "0 disables polling.")]
        public float SyncInterval = 2f;

        private CachingAssets _assets;
        private NovelShell _shell;
        private DownloadManager _downloads;
        private ContentSync _sync;
        private ILvnStateStore _state;   // stat/var persistence (local-first, optional server sync)
        private LvnChapter _currentChapter;
        private LvnTitle _currentTitle; // the playing title — for live per-title re-theming
        private string _currentScriptJson;
        private string _playerName;
        private LvnUiConfig _globalUi; // manifest.ui — the base for per-title theming
        private LvnManifest _manifest; // the live manifest (cross-chapter save routing)

        public CachingAssets Assets => _assets;
        public NovelShell Shell => _shell;

        private async void Start()
        {
            if (ShellTheme == null && !string.IsNullOrEmpty(ThemeResourcePath))
                ShellTheme = Resources.Load<ThemeStyleSheet>(ThemeResourcePath);

            var contentBase = ServerUrl;
            // Product services ride the same host; registration is idempotent
            // and a no-op offline — a pure-offline game just never signs in.
            Lvn.Services.LvnBackend.BaseUrl = ServerUrl;
#if UNITY_EDITOR
            // Editor test doubles: the 'dev' auth provider (server -auth-dev)
            // and an instantly-"watched" rewarded ad — the full sign-in and
            // ad-reward flows run end-to-end without any store SDKs. Real
            // builds: the host plugs LvnPlatformAuth.Google/Apple and
            // LvnAds.ShowRewarded (CAS.AI etc.) instead.
            Lvn.Services.LvnPlatformAuth.Dev ??=
                () => Task.FromResult("editor-dev-" + SystemInfo.deviceUniqueIdentifier);
            Lvn.Services.LvnAds.ShowRewarded ??= _ => Task.FromResult(true);
#endif
            _ = Lvn.Services.LvnBackend.EnsureRegisteredAsync();
            Lvn.Services.LvnServiceOps.RegisterAll(); // ext wallet_earn / leaderboard_submit / … from .lvns
            Lvn.Services.LvnAnalytics.Track("boot");
            if (OfflineBundled)
            {
                contentBase = LocalContentBase(BundleSubdir);
                SyncInterval = 0f; // nothing to poll — content is baked into the build
                Debug.Log($"[novelapp] offline bundle → {contentBase}");
            }

            _assets = new CachingAssets(contentBase);

            // Stat/var persistence: a bundled offline build keeps stats locally; a
            // server build syncs through /v1/state (local-first, so it still plays and
            // keeps stats when the server is down).
            _state = OfflineBundled
                ? (ILvnStateStore)new LocalStateStore()
                : new HttpStateStore(contentBase, ResolveUserId(), StateKey);

            // Connectivity gate (Liminal-style): probe the server with a hard 3s
            // deadline so an unreachable server falls straight through to the offline
            // path instead of hanging on a stuck socket. A local/bundled origin is
            // always reachable. The probe pins the global offline flag so every later
            // fetch fast-fails into the disk cache.
            bool online = _assets.Loader.IsLocal || await ProbeOnlineAsync();
            if (!online) LvnNetworkStatus.MarkOffline("boot healthz: server unreachable");
            Debug.Log($"[novelapp] connectivity → {(online ? "online" : "offline")}");

            try { await _assets.WarmVersionsAsync(); } catch { /* offline: last-known index */ }

            // Manifest: fresh from the server when online (cached for next time), else
            // the last cached copy — so a previously-online install still plays offline.
            LvnManifest manifest = null;
            if (online)
            {
                try { manifest = await FetchManifestAsync(); CacheManifest(manifest); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[novelapp] manifest fetch failed: {ex.Message} — falling back to cache");
                    online = false;
                    LvnNetworkStatus.MarkOffline("manifest fetch failed");
                }
            }
            if (manifest == null) manifest = LoadCachedManifest();
            if (manifest == null)
            {
                Debug.LogError("[novelapp] offline and no cached manifest — launch once online " +
                               "(or ship an offline bundle) to cache the novel for offline play");
                return;
            }
            Debug.Log($"[novelapp] manifest: {manifest.titles?.Count ?? 0} title(s) (online={online})");

            if (Stage == null) Stage = CreateStage();
            Stage.Assets = _assets;
            Stage.Catalog = new SpriteCatalog(manifest.sprites);
            // Theme the in-game dialogue/choices from the manifest, the same way
            // the shell screens read manifest.ui — so the whole game is themeable.
            // (A title can override this per-game; applied in PlayChapterAsync.)
            _globalUi = manifest.ui;
            _manifest = manifest;
            Stage.ApplyTheme(VnThemeBuilder.From(manifest.ui, Stage.Theme));
            Stage.CrossChapterLoader = CrossChapterLoadAsync;

            // Language: the manifest declares which catalogs exist (Settings shows
            // a picker when any); the reader's persisted choice wins over the
            // inspector default, and changing it mid-story reloads the catalog.
            LvnPrefs.AvailableLocales = manifest.languages != null && manifest.languages.Count > 0
                ? manifest.languages : System.Array.Empty<string>();
            if (!string.IsNullOrEmpty(LvnPrefs.Locale)) Locale = LvnPrefs.Locale;
            LvnPrefs.Changed -= OnPrefsMaybeLocale;
            LvnPrefs.Changed += OnPrefsMaybeLocale;

            _downloads = new DownloadManager(_assets.Loader);
            var prefetch = SafeBootPrefetch(manifest, online);

            _shell = NovelShell.Create(transform, 30, ShellTheme);
            _shell.Build(manifest, _assets);

            // The currency store: a quick-menu entry when the manifest opts in
            // (ui.store present), and the `ext store_show` op for scripts —
            // the story holds while the shop is open, then rolls on.
            var storeCfg = manifest.ui?.store;
            if (storeCfg != null && (storeCfg.show_menu_item ?? true))
                StageMenu.AddMenuItem(storeCfg.menu_label ?? "Store", stage => _ = _shell.OpenStoreAsync());
            Lvn.LvnOps.Register("store_show", (cmd, ctx) =>
            {
                ctx.Hold();
                _ = OpenStoreFromScriptAsync(ctx);
            });

            // The wardrobe: a quick-menu entry when any character has one (or
            // ui.wardrobe opts in explicitly), and `ext wardrobe_show char=id`.
            var wardrobeCfg = manifest.ui?.wardrobe;
            if ((wardrobeCfg != null || _shell.Wardrobe.Entities().Count > 0)
                && (wardrobeCfg?.show_menu_item ?? true))
                StageMenu.AddMenuItem(wardrobeCfg?.menu_label ?? "Wardrobe",
                    stage => _ = _shell.OpenWardrobeAsync());
            Lvn.LvnOps.Register("wardrobe_show", (cmd, ctx) =>
            {
                ctx.Hold();
                // Default: the in-story bottom sheet (the live actor is the
                // mirror). mode=full opens the full-screen overlay instead.
                _ = OpenWardrobeFromScriptAsync((string)cmd["char"], (string)cmd["mode"] == "full", ctx);
            });

            // The app-level settings screen: `ext settings_show` for scripts, and
            // an opt-in quick-menu entry (default OFF — the quick menu already has
            // its own in-game playback settings; set ui.settings.show_menu_item to
            // surface this fuller screen there too).
            var settingsCfg = manifest.ui?.settings;
            if (settingsCfg != null && (settingsCfg.show_menu_item ?? false))
                StageMenu.AddMenuItem(settingsCfg.menu_label ?? "Settings", stage => _ = _shell.OpenSettingsAsync());
            Lvn.LvnOps.Register("settings_show", (cmd, ctx) =>
            {
                ctx.Hold();
                _ = OpenSettingsFromScriptAsync(ctx);
            });

            // The long-press art view hides the stage's chrome; mirror it onto the
            // shell HUD (a separate UIDocument) so the WHOLE screen is just the scene.
            Stage.ChromeHiddenChanged += hidden =>
            {
                if (_shell?.Hud != null)
                    _shell.Hud.style.visibility = hidden
                        ? UnityEngine.UIElements.Visibility.Hidden
                        : UnityEngine.UIElements.Visibility.Visible;
            };

            // Live content sync — poll the version endpoint; reload on change.
            if (SyncInterval > 0f)
            {
                _sync = new ContentSync(_assets.Loader) { IntervalSeconds = SyncInterval };
                _sync.OnChanged += OnContentChanged;
                _sync.Start();
            }

            // Hub browse flow (ui.browse.layout = "hub"): unlock conditions read the
            // player's global stat flags; Play charges the title's entry cost; a
            // locked card explains itself with a popup.
            if (_shell.Hub != null)
            {
                _shell.Hub.GlobalStatsProvider = () => _state.LoadVarsAsync(GlobalScopeId, default);
                _shell.Hub.OnPlay = ChargeTitleEntryAsync;
                _shell.Hub.OnLockedHint = (name, hint) =>
                    _shell.AlertAsync(name, string.IsNullOrEmpty(hint) ? "Locked" : hint);
            }

            await _shell.RunAsync(
                bootReady: () => prefetch.IsCompleted,
                chapterReady: ch => () => true,
                chapterProgress: null,
                playChapter: PlayChapterAsync,
                askName: AskName);
        }

        // Charge a title's hub-entry cost (typically 1 energy for an expedition)
        // before it launches. Same store-retry flow as the per-chapter gate; free
        // when the title has no cost. Returns true if the player may enter.
        private async Task<bool> ChargeTitleEntryAsync(LvnTitle title)
        {
            var cost = title?.cost;
            if (cost == null || string.IsNullOrEmpty(cost.currency) || cost.amount <= 0) return true;

            string reason = "title:" + title.id;
            if (await Lvn.Services.LvnWallet.SpendAsync(cost.currency, cost.amount, reason)) return true;
            if (_shell == null) return false;

            var eco = _manifest?.economy;
            string title2 = eco?.gate_title ?? "Not enough energy";
            string msg = eco?.gate_message ?? "You need more to start this.";
            bool toStore = await _shell.ConfirmAsync(title2, msg,
                eco?.gate_buy ?? "Store", eco?.gate_cancel ?? "Not now");
            if (!toStore) return false;

            await _shell.OpenStoreAsync();
            await Lvn.Services.LvnWallet.RefreshAsync();
            if (await Lvn.Services.LvnWallet.SpendAsync(cost.currency, cost.amount, reason)) return true;

            await _shell.AlertAsync(eco?.gate_denied ?? title2, msg);
            return false;
        }

        private async Task OpenStoreFromScriptAsync(Lvn.ILvnOpContext ctx)
        {
            try { await _shell.OpenStoreAsync(); }
            finally { ctx.Resume(); }
        }

        private async Task OpenSettingsFromScriptAsync(Lvn.ILvnOpContext ctx)
        {
            try { await _shell.OpenSettingsAsync(); }
            finally { ctx.Resume(); }
        }

        // The in-story sheet as CONTENT of the stage's shared window: the
        // dialogue fades out, the same-skinned frame slides up with the
        // wardrobe inside — one panel, native transitions (no overlay pop).
        private WardrobeSheet _storySheet;

        private async Task OpenWardrobeFromScriptAsync(string entity, bool full, Lvn.ILvnOpContext ctx)
        {
            try
            {
                if (full) { await _shell.OpenWardrobeAsync(entity); return; }
                if (_storySheet == null)
                {
                    var ui = _manifest?.ui ?? new LvnUiConfig();
                    _storySheet = new WardrobeSheet(ui.wardrobe, ui.dialogue, ui.choices, _assets, hosted: true);
                    _storySheet.SetManifest(_manifest);
                    _storySheet.OpenStore = () => _shell.OpenStoreAsync();
                }
                var done = _storySheet.ShowAsync(entity);   // logic only — the host animates
                await Stage.ShowPanelAsync(_storySheet);    // dialogue fades, frame slides up
                try { await done; }
                finally { await Stage.HidePanelAsync(); }   // frame slides away, dialogue returns
            }
            finally { ctx.Resume(); }
        }

        // Builds a VnStage on a child GameObject with its own UIDocument + panel
        // (sortingOrder below the shell's 30) so dropping a single NovelApp on an
        // empty object is enough to run the whole flow.
        private VnStage CreateStage()
        {
            var go = new GameObject("VnStage");
            go.transform.SetParent(transform, false);
            // Configure while inactive so OnEnable/Build runs only after every field
            // (notably UseCanvasScene) is set — otherwise Build() would read the
            // default and pick the wrong scene renderer.
            go.SetActive(false);
            var doc = go.AddComponent<UIDocument>();
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.name = "VnStagePanel";
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1080, 1920);
            ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            ps.match = 0.5f;
            ps.sortingOrder = 10;
            if (ShellTheme != null) ps.themeStyleSheet = ShellTheme;
            doc.panelSettings = ps;
            var stage = go.AddComponent<VnStage>();
            // Render the scene (bg + actors + camera) on a uGUI Canvas below this
            // UITK panel — the 60fps / Spine path. Dialogue & choices stay on UITK
            // above it. The shell content uses no click-hotspots or actor enter/exit
            // transitions (the features not yet on the Canvas path), so this is safe.
            stage.UseCanvasScene = true;
            go.SetActive(true);
            return stage;
        }

        // Build the platform-correct content base for a StreamingAssets bundle.
        // Android already yields a jar:file:// url that UnityWebRequest reads
        // straight from the APK; desktop/iOS need an explicit file:// scheme.
        private static string LocalContentBase(string sub)
        {
            var p = Application.streamingAssetsPath;
            if (!string.IsNullOrEmpty(sub)) p += "/" + sub.Trim('/');
            return p.Contains("://") ? p : "file://" + p;
        }

        // Load a chapter's localization catalog (text_id → string) for the active
        // Locale from <script>.<locale>.json. Best-effort: missing catalog → null,
        // so the chapter falls back to its inline text.
        private async Task<System.Collections.Generic.IReadOnlyDictionary<string, string>> LoadCatalogAsync(string scriptUrl)
        {
            if (string.IsNullOrEmpty(Locale) || string.IsNullOrEmpty(scriptUrl)) return null;
            var baseUrl = scriptUrl.EndsWith(".lvn") ? scriptUrl.Substring(0, scriptUrl.Length - 4) : scriptUrl;
            var url = baseUrl + "." + Locale + ".json";
            try
            {
                var json = await _assets.Loader.DownloadScriptText(url, default, singleAttempt: true);
                if (string.IsNullOrEmpty(json)) return null;
                return Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(json);
            }
            catch { return null; }
        }

        private async Task<LvnManifest> FetchManifestAsync()
        {
            var json = await _assets.Loader.DownloadScriptText("/v1/content/manifest", default, singleAttempt: true);
            return Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json) ?? new LvnManifest();
        }

        private async Task SafeBootPrefetch(LvnManifest manifest, bool online)
        {
            // Online: verify + download the boot set. Offline: warm only what's
            // already on disk (no network), so a cached install still shows its art.
            try { await _downloads.BootPrefetchAsync(manifest, online, default); }
            catch { /* best-effort — missing boot art is non-fatal */ }
        }

        // Probe the server's /healthz with a hard 3s deadline. Token-based, because
        // UnityWebRequest.timeout doesn't reliably interrupt a DNS/TLS stall — the
        // difference between an instant offline fallback and a ~30s boot hang.
        private async Task<bool> ProbeOnlineAsync()
        {
            try
            {
                using var probe = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                return await _assets.Loader.HealthzAsync("/healthz", probe.Token);
            }
            catch { return false; }
        }

        // ── Offline manifest cache ───────────────────────────────────────────────
        // The manifest is cached locally on every successful online fetch and read
        // back when the server is unreachable, so a previously-online install boots
        // straight into the menu offline (chapters then play from the disk cache).
        private const string ManifestCacheKey = "lvn_manifest_cache";

        private static void CacheManifest(LvnManifest m)
        {
            if (m == null) return;
            try
            {
                PlayerPrefs.SetString(ManifestCacheKey, Newtonsoft.Json.JsonConvert.SerializeObject(m));
                PlayerPrefs.Save();
            }
            catch { /* cache write best-effort */ }
        }

        private static LvnManifest LoadCachedManifest()
        {
            try
            {
                var json = PlayerPrefs.GetString(ManifestCacheKey, null);
                return string.IsNullOrEmpty(json)
                    ? null
                    : Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json);
            }
            catch { return null; }
        }

        // Play a title from its entry point and KEEP GOING: when a chapter finishes,
        // the next one (by number) follows seamlessly — the player reads the whole
        // novel without bouncing off the carousel between episodes. A progress
        // marker remembers the furthest chapter started, so re-entering the title
        // continues there (and the in-chapter autosave restores the exact line);
        // finishing the last chapter clears it so a replay starts clean.
        private async Task PlayChapterAsync(LvnTitle title, LvnChapter chapter, string playerName)
        {
            var resume = LvnProgress.Current(title);
            // Resuming a chapter the player already paid to enter must not charge
            // again; only fresh entries (this fresh start and every `next`) do.
            bool alreadyEntered = resume != null;
            if (resume != null) chapter = resume;
            while (chapter != null)
            {
                if (!alreadyEntered && !await ChargeChapterEntryAsync(chapter))
                    break; // couldn't/wouldn't pay the entry cost → back to the carousel
                alreadyEntered = false;
                LvnProgress.SetCurrent(title, chapter);
                ChapterStarted?.Invoke(title, chapter);
                Lvn.Services.LvnAnalytics.Track("chapter_start",
                    ("title", title?.id), ("chapter", chapter.id));
                var finished = await PlayOneChapterAsync(title, chapter, playerName);
                if (finished == null) break; // left mid-chapter (cancel/error) → carousel
                ChapterFinished?.Invoke(title, finished);
                Lvn.Services.LvnAnalytics.Track("chapter_finish",
                    ("title", title?.id), ("chapter", finished.id));
                // A cross-chapter save load can land the player in another title —
                // continue along whichever title the finished chapter belongs to.
                var (owner, _) = FindChapterByScriptUrl(finished.script_url);
                if (owner != null) title = owner;
                var next = NextChapterOf(title, finished);
                if (next == null)
                {
                    LvnProgress.ClearCurrent(title); // the novel is complete — replays restart
                    break;
                }
                chapter = next;
            }
        }

        // Charge the chapter-entry currency (typically the regenerating "energy")
        // before a fresh chapter loads. Returns true when the player may enter:
        // the gate is disabled, the chapter is free, the spend succeeded, or a
        // store purchase covered it. On a hard refusal (no funds and no/failed
        // purchase) shows a popup and returns false, dropping back to the carousel.
        private async Task<bool> ChargeChapterEntryAsync(LvnChapter chapter)
        {
            var eco = _manifest?.economy;
            var currency = eco?.chapter_currency;
            int cost = eco?.chapter_cost ?? 1;
            if (string.IsNullOrEmpty(currency) || cost <= 0) return true; // gate off
            if (eco.free_chapters != null && chapter != null && eco.free_chapters.Contains(chapter.id))
                return true; // this chapter is on the house

            string reason = "chapter:" + chapter?.id;
            if (await Lvn.Services.LvnWallet.SpendAsync(currency, cost, reason)) return true;

            // Not enough — offer the store, then retry the spend once.
            if (_shell == null) return false;
            string title = eco.gate_title ?? "Not enough energy";
            string msg = eco.gate_message ?? "You need more to open this chapter.";
            bool toStore = await _shell.ConfirmAsync(title, msg,
                eco.gate_buy ?? "Store", eco.gate_cancel ?? "Not now");
            if (!toStore) return false;

            await _shell.OpenStoreAsync();
            await Lvn.Services.LvnWallet.RefreshAsync();
            if (await Lvn.Services.LvnWallet.SpendAsync(currency, cost, reason)) return true;

            await _shell.AlertAsync(eco.gate_denied ?? title, msg);
            return false;
        }

        // The next chapter by number, or null when this was the last one.
        private static LvnChapter NextChapterOf(LvnTitle title, LvnChapter current)
        {
            if (title?.seasons == null || current == null) return null;
            LvnChapter best = null;
            foreach (var s in title.seasons)
            {
                if (s?.chapters == null) continue;
                foreach (var c in s.chapters)
                {
                    if (c == null || c.number <= current.number) continue;
                    if (best == null || c.number < best.number) best = c;
                }
            }
            return best;
        }

        // Stream one chapter's script and run it through the VnStage, driving the
        // HUD until it ends. Returns the chapter that actually FINISHED (it can
        // differ from the requested one — a cross-chapter save load switches the
        // stage mid-play), or null when the player left mid-chapter.
        private async Task<LvnChapter> PlayOneChapterAsync(LvnTitle title, LvnChapter chapter, string playerName)
        {
            if (Stage == null || chapter == null || string.IsNullOrEmpty(chapter.script_url))
            {
                await Task.Delay(400);
                return null;
            }

            // Clean the stage at the START too — not just on the previous chapter's
            // end — so a leftover actor/animation never lingers while this chapter's
            // script is still downloading.
            Stage.ClearStage();

            // Per-title theme: engine defaults → global manifest.ui → this title's ui.
            // Rebuilt fresh each entry so a previous title's look never leaks in.
            var theme = VnThemeBuilder.From(_globalUi, new VnTheme());
            if (title?.ui != null) theme = VnThemeBuilder.From(title.ui, theme);
            Stage.ApplyTheme(theme);

            // Offline decision layer (ported from the Liminal client): decide how
            // to enter the chapter from connectivity + what's on disk. A local
            // bundle reports everything cached/reachable, so it plays instantly;
            // an online client degrades gracefully and never hangs.
            bool online = _assets.Loader.IsLocal || !LvnNetworkStatus.IsOffline;
            var readiness = OfflinePolicy.ComputeReadiness(
                _assets.Loader.IsScriptCached(chapter.script_url),
                chapter.assets,
                _assets.Loader.IsAssetCached);
            var plan = ChapterEntryPlan.From(online, in readiness);
            if (!plan.CanPlay)
            {
                Debug.LogWarning($"[novelapp] chapter '{chapter.id}' unavailable offline (script not cached)");
                await Task.Delay(300);
                return null;
            }

            string json;
            try { json = await _assets.Loader.DownloadScriptCached(chapter.script_url); }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] script fetch failed: {ex.Message}"); return null; }
            if (string.IsNullOrEmpty(json)) { Debug.LogWarning($"[novelapp] no script for '{chapter.id}'"); return null; }

            _currentChapter = chapter;
            _currentTitle = title;
            _playerName = playerName;
            _currentScriptJson = json;
            Stage.Strings = await LoadCatalogAsync(chapter.script_url); // localization (null → inline text)
            // Carry this title's persisted stats into the chapter (relationships, route,
            // memory flags…). The imported global defaults are `default:true`, so they
            // don't overwrite these; a fresh game starts empty. The store is local-first
            // (offline-safe) and, when a server is configured, syncs through /v1/state.
            Stage.SeedVars = await LoadScopedVarsAsync(title?.id);

            // The genre-standard restart semantics: picking a chapter from the
            // picker resets the variables to what they were when that chapter was
            // FIRST entered — stats from the future must not leak into the past
            // and mis-gate its choices. The live state store rolls back with it,
            // so a later stat sync doesn't resurrect the discarded future.
            bool restart = LvnProgress.TakeRestart(title?.id, chapter.id);
            if (restart)
            {
                Stage.SeedVars = LvnProgress.Checkpoint(title?.id, chapter.id)
                                 ?? new Newtonsoft.Json.Linq.JObject();
                // Global (cross-novel) stats must NOT roll back with a per-chapter
                // restart — overlay the CURRENT global stats over the checkpoint.
                var curGlobal = await _state.LoadVarsAsync(GlobalScopeId, default);
                if (curGlobal != null && curGlobal.Count > 0) Stage.SeedVars[GlobalVar] = curGlobal;
                await SaveScopedVarsAsync(title?.id, Stage.SeedVars);
                LvnSaveStore.Delete(title?.id, LvnSaveStore.AutoSlot);
                Debug.Log($"[novelapp] restarting '{chapter.id}' from its entry checkpoint");
            }

            // Resume where the player actually was: a mid-chapter autosave for THIS
            // script (written on choices/every few lines/app pause) beats replaying
            // the chapter from the top. A finished chapter's autosave was deleted on
            // OnEnd, so replays start clean.
            var autosave = LvnSaveStore.Get(title?.id, LvnSaveStore.AutoSlot);
            bool resuming = !restart && autosave?.Snap != null
                            && autosave.Snap.ScriptUrl == chapter.script_url
                            && !autosave.Snap.Finished;

            // A FRESH entry (chapter transition, picker restart, first launch) is
            // the moment the entry checkpoint captures; a mid-chapter resume must
            // NOT overwrite it with mid-chapter stats.
            if (!resuming)
                LvnProgress.SaveCheckpoint(title?.id, chapter.id, Stage.SeedVars);

            Stage.SetSaveContext(title?.id, chapter.id, chapter.script_url);
            Stage.Gallery = title?.gallery;
            Stage.Play(json, warmIntroSpine: !resuming); // resume restores below — don't run/warm the intro
            if (Stage.Player != null && !string.IsNullOrEmpty(playerName))
                Stage.Player.Vars["player"] = playerName;

            if (resuming)
            {
                Debug.Log($"[novelapp] resuming '{chapter.id}' from autosave (@{autosave.Snap.Index})");
                Stage.RestoreSnapshot(autosave.Snap);
                if (Stage.Player != null && !string.IsNullOrEmpty(playerName))
                    Stage.Player.Vars["player"] = playerName;
            }

            // Drive the HUD percent until the chapter ends — or the player asks
            // out (the quick menu's Exit; position already autosaved, so the
            // carousel's Continue leads straight back to this line).
            while (Stage.Player != null && !Stage.Player.Finished && !Stage.ExitRequested)
            {
                _shell.Hud.SetProgress(Stage.Player.ProgressIndex, Stage.Player.Count);
                try { await Task.Yield(); }
                catch (OperationCanceledException) { break; }
            }
            bool exited = Stage.ExitRequested;
            Stage.ClearExitRequest();
            if (exited) Stage.ClearStage(); // leave nothing behind under the carousel
            // Persist the chapter's ending state so the next chapter (and the next
            // session) resume with the same stats — whether it finished or the player
            // left mid-chapter (the loop also breaks on cancellation).
            if (Stage.Player != null) await SaveScopedVarsAsync(title?.id, VarsToJObject(Stage.Player.Vars));
            _shell.Hud.SetProgress(1, 1);
            // The chapter that actually played to the end — a cross-chapter save
            // load may have switched the stage away from the requested one.
            bool finished = Stage.Player != null && Stage.Player.Finished;
            var played = _currentChapter ?? chapter;
            _currentChapter = null;
            _currentTitle = null;
            // Free the finished chapter's decoded art (a chapter can hold dozens of
            // full-res RGBA sprites). UI art — covers, theme skins under ui/ — stays
            // warm; the disk cache is intact so the next entry re-decodes quickly.
            _assets.Loader.UnloadWhere(u => u.Contains("/art/") || u.Contains("/bg/"));
            return finished ? played : null;
        }

        // Cross-chapter save routing: a slot taken in another chapter resolves to
        // its chapter by script url, fetches that script, plays it and restores —
        // all in place, while the shell's play-loop keeps driving whatever player
        // the stage currently holds. Wired into VnStage.CrossChapterLoader.
        private async Task<bool> CrossChapterLoadAsync(LvnSaveSlot slot)
        {
            var url = slot?.Snap?.ScriptUrl;
            if (string.IsNullOrEmpty(url) || Stage == null) return false;
            var (title, chapter) = FindChapterByScriptUrl(url);
            if (chapter == null)
            {
                Debug.LogWarning($"[novelapp] save points at unknown chapter: {url}");
                return false;
            }

            string json;
            try { json = await _assets.Loader.DownloadScriptCached(url); }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] cross-chapter fetch failed: {ex.Message}"); return false; }
            if (string.IsNullOrEmpty(json)) return false;

            Stage.ClearStage();
            Stage.Strings = await LoadCatalogAsync(url);
            Stage.SeedVars = await LoadScopedVarsAsync(title?.id);
            Stage.SetSaveContext(title?.id, chapter.id, url);
            Stage.Gallery = title?.gallery;
            Stage.Play(json, warmIntroSpine: false); // the restore below advances
            if (Stage.Player != null && !string.IsNullOrEmpty(_playerName))
                Stage.Player.Vars["player"] = _playerName;
            Stage.RestoreSnapshot(slot.Snap);
            _currentChapter = chapter;
            _currentTitle = title ?? _currentTitle;
            _currentScriptJson = json;
            LvnProgress.SetCurrent(_currentTitle, chapter); // continue follows the jump
            Debug.Log($"[novelapp] loaded save into '{chapter.id}' (@{slot.Snap.Index})");
            return true;
        }

        private (LvnTitle title, LvnChapter chapter) FindChapterByScriptUrl(string scriptUrl)
        {
            if (_manifest?.titles == null) return (null, null);
            foreach (var t in _manifest.titles)
            {
                if (t?.seasons == null) continue;
                foreach (var s in t.seasons)
                {
                    if (s?.chapters == null) continue;
                    foreach (var c in s.chapters)
                        if (c != null && c.script_url == scriptUrl)
                            return (t, c);
                }
            }
            return (null, null);
        }

        // The save identity for /v1/state. An explicit UserId (an account) wins; else
        // a per-device id generated once and kept in PlayerPrefs.
        private string ResolveUserId()
        {
            if (!string.IsNullOrEmpty(UserId)) return UserId;
            var id = PlayerPrefs.GetString("lvn_user", "");
            if (string.IsNullOrEmpty(id))
            {
                id = System.Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString("lvn_user", id);
                PlayerPrefs.Save();
            }
            return id;
        }

        // The cross-novel player-stat namespace. Stats under the `global` var
        // (scripts: `set/inc key="global.<stat>"`, read `global.<stat>`) persist to
        // a per-player state blob shared by EVERY novel, so they accumulate across
        // titles and one novel can read what another left behind. Ordinary vars stay
        // scoped to their title.
        private const string GlobalVar = "global";
        private const string GlobalScopeId = "__global";

        // Load a title's stats plus the player's global stats, merged into one seed
        // (global stats land under the `global` var). Two blobs, one per scope.
        private async Task<Newtonsoft.Json.Linq.JObject> LoadScopedVarsAsync(string titleId)
        {
            var vars = await _state.LoadVarsAsync(titleId, default) ?? new Newtonsoft.Json.Linq.JObject();
            var global = await _state.LoadVarsAsync(GlobalScopeId, default);
            if (global != null && global.Count > 0) vars[GlobalVar] = global;
            return vars;
        }

        // Persist ending stats, splitting the `global` namespace out to its own
        // per-player blob so it survives beyond this novel.
        private async Task SaveScopedVarsAsync(string titleId, Newtonsoft.Json.Linq.JObject vars)
        {
            if (vars == null) return;
            if (vars[GlobalVar] is Newtonsoft.Json.Linq.JObject global)
            {
                vars = (Newtonsoft.Json.Linq.JObject)vars.DeepClone(); // don't mutate the caller's live vars
                vars.Remove(GlobalVar);
                await _state.SaveVarsAsync(GlobalScopeId, global, default);
            }
            await _state.SaveVarsAsync(titleId, vars, default);
        }

        // Snapshot the player's live variables as a JObject the state store persists.
        private static Newtonsoft.Json.Linq.JObject VarsToJObject(
            System.Collections.Generic.IReadOnlyDictionary<string, Newtonsoft.Json.Linq.JToken> vars)
        {
            var jo = new Newtonsoft.Json.Linq.JObject();
            if (vars != null)
                foreach (var kv in vars)
                    jo[kv.Key] = kv.Value?.DeepClone();
            return jo;
        }

        // Mobile: persist stats when the app is backgrounded / quit mid-chapter.
        // Fire-and-forget — the store writes its LOCAL cache synchronously before the
        // first await, so stats are safe even if the process is suspended immediately.
        private void OnApplicationPause(bool paused)
        {
            if (paused && _state != null && Stage?.Player != null && _currentTitle != null)
                _ = SaveScopedVarsAsync(_currentTitle.id, VarsToJObject(Stage.Player.Vars));
            // Position too, not just stats — so a suspended app resumes on the same
            // line (the autosave slot; SaveToSlot is synchronous PlayerPrefs).
            if (paused) Stage?.AutosaveNow();
        }

        // Server content changed: refresh the version index, re-apply the manifest
        // (carousel rebuilds), and hot-reload the open chapter if its script moved.
        private async void OnContentChanged()
        {
            Debug.Log("[novelapp] content changed — reloading");
            try { await _assets.WarmVersionsAsync(); } catch { /* offline */ }

            LvnManifest manifest;
            try { manifest = await FetchManifestAsync(); }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] live manifest fetch failed: {ex.Message}"); return; }
            CacheManifest(manifest); // keep the offline copy fresh on every live update
            // Pull the changed boot-set bytes and re-warm replaced covers BEFORE the
            // carousel rebuilds — otherwise it re-renders from the stale in-memory
            // sprites and a cover swap on the server never shows up.
            try { await _downloads.MenuRefreshAsync(manifest, default); }
            catch { /* best-effort; never blocks the live update */ }
            _shell?.ApplyLiveUpdate(manifest);
            _storySheet?.SetManifest(manifest); // the in-story wardrobe follows live edits too
            _globalUi = manifest.ui;
            _manifest = manifest; // cross-chapter routing follows the live manifest
            if (Stage != null)
            {
                Stage.Catalog = new SpriteCatalog(manifest.sprites);
                // Re-theme live — rebuilt fresh from the NEW manifest: engine
                // defaults → global ui → the playing title's ui override (matched
                // by id in the new manifest, so per-title edits take effect). Safe
                // mid-line: VnStage.ApplyTheme restores the visible line/choices.
                var theme = VnThemeBuilder.From(manifest.ui, new VnTheme());
                LvnTitle liveTitle = null;
                if (_currentTitle != null && manifest.titles != null)
                    liveTitle = manifest.titles.Find(t => t != null && t.id == _currentTitle.id);
                if (liveTitle?.ui != null) theme = VnThemeBuilder.From(liveTitle.ui, theme);
                Stage.ApplyTheme(theme);
            }

            if (_currentChapter == null || Stage == null || Stage.Player == null || Stage.Player.Finished)
                return;

            // Fetch the script FRESH (not the version-pinned disk cache, which can
            // hand back the old text when reacting to a live edit — the whole point
            // here is to apply what just changed). The disk cache is refreshed in
            // the background so an offline replay of the new version still works.
            string json;
            try { json = await _assets.Loader.DownloadScriptText(_currentChapter.script_url); }
            catch { return; }
            if (string.IsNullOrEmpty(json)) return;
            if (json == _currentScriptJson)
            {
                // The script didn't change — only assets did (a replaced sprite or
                // background). Re-apply the visible stage in place so the new art shows
                // live, without restarting the chapter. The version index was just
                // re-warmed, so each sprite reloads under its new content hash.
                if (Stage.Player != null && !Stage.Player.Finished)
                    Stage.Player.ReplayVisuals(Stage.Player.Index + 1);
                return;
            }
            _assets.Loader.RefreshScriptInBackground(_currentChapter.script_url);

            _currentScriptJson = json;
            // A non-structural edit (reworded line, tweaked emotion/position) keeps
            // the chapter playing exactly where it is; only a changed command
            // structure forces a restart from the top.
            if (Stage.TryHotSwap(json))
            {
                Debug.Log($"[novelapp] hot-swapped chapter '{_currentChapter.id}' in place (kept position)");
            }
            else
            {
                Stage.Play(json);
                if (Stage.Player != null && !string.IsNullOrEmpty(_playerName))
                    Stage.Player.Vars["player"] = _playerName;
                Debug.Log($"[novelapp] reloaded chapter '{_currentChapter.id}' (structure changed — restarted)");
            }
        }

        private void OnDestroy()
        {
            _sync?.Stop();
            LvnPrefs.Changed -= OnPrefsMaybeLocale;
        }

        // The Settings language row writes LvnPrefs.Locale; pick the change up
        // and swap the running chapter's string catalog — new lines render in
        // the new language immediately (the visible line updates on advance).
        private async void OnPrefsMaybeLocale()
        {
            var want = LvnPrefs.Locale;
            if (want == Locale) return;
            Locale = want;
            if (_currentChapter != null && Stage != null)
            {
                try { Stage.Strings = await LoadCatalogAsync(_currentChapter.script_url); }
                catch { Stage.Strings = null; } // no catalog → the inline original
            }
        }
    }
}
