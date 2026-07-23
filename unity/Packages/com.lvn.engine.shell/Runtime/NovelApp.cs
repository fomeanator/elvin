using System;
using System.Collections.Generic;
using System.Linq;
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

        // Title-level variable declarations (title.vars_url), cached per title:
        // "game" keys persist across chapters, "chapter" keys reset on every
        // fresh chapter entry. Replaces the per-chapter default-set boilerplate.
        private sealed class TitleVars
        {
            public Newtonsoft.Json.Linq.JObject game;
            public Newtonsoft.Json.Linq.JObject chapter;
        }
        private readonly Dictionary<string, TitleVars> _titleVarsCache = new Dictionary<string, TitleVars>();

        private async Task<TitleVars> LoadTitleVarsAsync(LvnTitle title)
        {
            if (string.IsNullOrEmpty(title?.vars_url)) return null;
            if (_titleVarsCache.TryGetValue(title.id, out var hit)) return hit;
            TitleVars tv = null;
            try
            {
                var json = await _assets.Loader.DownloadScriptText(title.vars_url, destroyCancellationToken);
                var root = Newtonsoft.Json.Linq.JObject.Parse(json);
                tv = new TitleVars
                {
                    game = root["game"] as Newtonsoft.Json.Linq.JObject,
                    chapter = root["chapter"] as Newtonsoft.Json.Linq.JObject,
                };
            }
            catch (Exception e)
            {
                // Declarations are an optimization, never a gate: chapters keep
                // playing on their own (older content carries inline defaults).
                Debug.LogWarning($"[novelapp] vars_url '{title.vars_url}' failed: {e.Message}");
            }
            _titleVarsCache[title.id] = tv; // cache the miss too — no refetch storm
            return tv;
        }

        public CachingAssets Assets => _assets;
        public NovelShell Shell => _shell;

        private async void Start()
        {
            // Boot telemetry: one stopwatch, a mark per phase — `adb logcat -s
            // Unity | grep lvn-boot` (or the editor console) reads as a boot
            // profile. Anything that grows here is a regression to hunt.
            var bootClock = System.Diagnostics.Stopwatch.StartNew();
            void Mark(string phase) => Debug.Log($"[lvn-boot] +{bootClock.ElapsedMilliseconds}ms {phase}");

            // Test-lane server override (Development builds only): device
            // automation points this install at a throwaway server via
            // `am start … -e lvn_server <url>` (or LVN_SERVER for CI players)
            // instead of re-exporting. Must land before ANYTHING derives from
            // ServerUrl — the log shipper, content base and state store all do.
            var serverOverride = LvnLaunchOverrides.ServerUrl();
            if (serverOverride != null)
            {
                ServerUrl = serverOverride;
                Debug.Log($"[novelapp] server override (dev): {ServerUrl}");
            }

            // Field diagnostics BEFORE the first mark: errors, exceptions and
            // the [lvn-boot]/[lvn-perf] marks ship to /v1/log/client — a partner
            // device's crash is readable via /v1/admin/client-logs, no adb.
            Lvn.Services.LvnBackend.BaseUrl = ServerUrl;
            Lvn.Services.LvnLogShip.Boot();

            // The theme must land on the shared panel BEFORE the veil: a panel
            // without a ThemeStyleSheet has no default font, so every veil
            // label renders as NOTHING — the "black screen with no text" class
            // of bug. (The shell used to set it only after the manifest.)
            if (ShellTheme == null && !string.IsNullOrEmpty(ThemeResourcePath))
                ShellTheme = Resources.Load<ThemeStyleSheet>(ThemeResourcePath);
            LvnPanel.SetTheme(ShellTheme);

            // First paint THIS frame — before any network round-trip — so the
            // device never sits on a raw black screen while boot works.
            BootVeil.Show();
            Mark("veil up (first paint)");
            // Let the veil actually REACH the screen before any heavier boot
            // work (PSO load, probes): on slow devices frame 1's render was
            // getting starved and the first visible percent was already 30.
            await Task.Yield();
            await Task.Yield();

            // PSO precook: warms last session's traced pipeline states behind
            // the boot screen (first launch traces instead) — kills the
            // first-show shader-compile hitches. Fire-and-forget, self-paced.
            LvnPsoWarmup.Boot();

            // Product services ride the same host (BaseUrl set above, before the
            // log shipper); registration is idempotent and a no-op offline — a
            // pure-offline game just never signs in.
            var contentBase = ServerUrl;
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
            //
            // All three boot round-trips fly TOGETHER — healthz, the version
            // index and the manifest are independent GETs, and running them
            // serially was the single biggest boot cost on device (3 × mobile
            // RTT; the old worst case even ate the probe's full 3s deadline
            // before the first byte of manifest moved).
            var probeTask = _assets.Loader.IsLocal ? Task.FromResult(true) : ProbeOnlineAsync();
            var versionsTask = _assets.WarmVersionsAsync();
            var manifestTask = FetchManifestAsync();
            BootVeil.Progress(10, "подключение…");

            bool online = await probeTask;
            if (!online) LvnNetworkStatus.MarkOffline("boot healthz: server unreachable");
            Mark($"connectivity → {(online ? "online" : "offline")}");
            BootVeil.Progress(30, "загрузка данных…");

            try { await versionsTask; } catch { /* offline: last-known index */ }
            Mark("version index");

            // Manifest: fresh from the server when online (cached for next time), else
            // the last cached copy — so a previously-online install still plays offline.
            LvnManifest manifest = null;
            if (online)
            {
                try { manifest = await manifestTask; CacheManifest(manifest); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[novelapp] manifest fetch failed: {ex.Message} — falling back to cache");
                    online = false;
                    LvnNetworkStatus.MarkOffline("manifest fetch failed");
                }
            }
            else
                // The in-flight fetch will fail on its own timeline; observe the
                // fault so it can't surface as an unobserved-exception warning.
                _ = manifestTask.ContinueWith(t => _ = t.Exception,
                    TaskContinuationOptions.OnlyOnFaulted);
            if (manifest == null) manifest = LoadCachedManifest();
            Mark("manifest");
            BootVeil.Progress(60);
            if (manifest == null)
            {
                // The probe may have lied (its 3s deadline lost to a slow first
                // launch) while the manifest fetch itself was about to succeed —
                // give the in-flight task its chance before slow retries.
                try
                {
                    manifest = await manifestTask;
                    CacheManifest(manifest);
                    online = true;
                    LvnNetworkStatus.MarkOnline("boot manifest arrived despite failed probe");
                }
                catch { /* genuinely unreachable — recovery loop below */ }
            }
            if (manifest == null)
            {
                // A fresh install that can't reach the server is NOT a dead end:
                // hold on the veil and keep retrying — the moment the network
                // appears the app boots itself, no restart needed.
                Debug.LogWarning("[novelapp] no manifest and no cache — holding boot for connectivity");
                for (int attempt = 1; manifest == null; attempt++)
                {
                    BootVeil.Status($"нет соединения с сервером — переподключение… ({attempt})");
                    try { await Task.Delay(5000, destroyCancellationToken); }
                    catch (OperationCanceledException) { return; }
                    try
                    {
                        manifest = await FetchManifestAsync();
                        CacheManifest(manifest);
                        online = true;
                        LvnNetworkStatus.MarkOnline("boot manifest retry succeeded");
                    }
                    catch (Exception ex)
                    {
                        Debug.Log($"[novelapp] manifest retry {attempt}: {ex.Message}");
                    }
                }
                Mark("manifest (recovered)");
                BootVeil.Progress(60, "");
            }
            // The awaits above outlive a destroyed host (scene switch, embedder
            // teardown) — never keep booting on a dead component.
            if (destroyCancellationToken.IsCancellationRequested) return;
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

            Mark("stage + theme ready");
            _downloads = new DownloadManager(_assets.Loader);
            var prefetch = SafeBootPrefetch(manifest, online);
            _ = prefetch.ContinueWith(_ => Debug.Log(
                $"[lvn-boot] +{bootClock.ElapsedMilliseconds}ms boot prefetch settled (background)"),
                TaskScheduler.FromCurrentSynchronizationContext());

            // Progress vault: a VIRGIN install (corrupted prefs, a reinstall
            // under the same identity) gets the player's progress re-planted —
            // file home first (instant, offline), then the server backup —
            // BEFORE the hub renders, so «Продолжить» is right from frame one.
            try
            {
                if (ProgressVault.IsVirgin(manifest))
                {
                    ProgressVault.Apply(ProgressVault.ReadLocal(), manifest);
                    if (ProgressVault.IsVirgin(manifest) && _state != null)
                        ProgressVault.Apply(
                            await _state.LoadVarsAsync(ProgressVault.Scope, destroyCancellationToken),
                            manifest);
                }
            }
            catch (Exception e) { Debug.LogWarning("[vault] restore skipped: " + e.Message); }

            _shell = NovelShell.Create(transform, 30, ShellTheme);
            _shell.Build(manifest, _assets);
            Mark("shell built");

            // The currency store: a quick-menu entry when the manifest opts in
            // (ui.store present), and the `ext store_show` op for scripts —
            // the story holds while the shop is open, then rolls on.
            var storeCfg = manifest.ui?.store;
            if (storeCfg != null && (storeCfg.show_menu_item ?? true))
                StageMenu.AddMenuItem(storeCfg.menu_label ?? "Store", stage => _ = _shell.OpenPackShopAsync());
            Lvn.LvnOps.Register("store_show", (cmd, ctx) =>
            {
                ctx.Hold();
                _ = OpenStoreFromScriptAsync(ctx);
            });

            // The wardrobe: a quick-menu entry when any character has one (or
            // ui.wardrobe opts in explicitly), and `ext wardrobe_show char=id`.
            // The menu entry opens the IN-STORY sheet over the live scene — the
            // hero dressed against the current background, the same experience
            // as a story wardrobe moment, but always reachable.
            var wardrobeCfg = manifest.ui?.wardrobe;
            if ((wardrobeCfg != null || AnyWardrobeEntity())
                && (wardrobeCfg?.show_menu_item ?? true))
                StageMenu.AddMenuItem(wardrobeCfg?.menu_label ?? "Wardrobe",
                    stage => _ = OpenWardrobeFromMenuAsync(stage));
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

            // Wallet-priced choices (imported "[premium]" options carry
            // wallet_cost): route the spend through the product wallet. A failed
            // spend keeps the menu up — the stage shows a "not enough" hint.
            Stage.ChoiceSpend = (currency, amount) =>
                Lvn.Services.LvnWallet.SpendAsync(currency, amount, "choice");

            // Test-build currency faucet (economy.debug_grant): a quick-menu item
            // that credits the wallet on tap — the partner's "получить 100" button
            // for exercising paid choices and the wardrobe without a store.
            var faucet = manifest.economy?.debug_grant;
            if (faucet != null && !string.IsNullOrEmpty(faucet.currency))
            {
                int amount = faucet.amount ?? 100;
                string label = faucet.label ?? $"Получить {amount}";
                StageMenu.AddMenuItem(label, stage => _ = GrantFaucetAsync(faucet.currency, amount));
            }
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

            // ui.hud.mode == "choices": corner-minimal reading — the HUD stays off
            // the reading surface and surfaces exactly while a choice is up (the
            // one moment costs and balances matter). Chapter end hides it again
            // via the shell's normal Hide(Hud).
            if (_shell.HudChoicesOnly)
                Stage.ChoicesVisibleChanged += visible =>
                {
                    if (_shell?.Hud != null)
                        _shell.Hud.style.display = visible
                            ? UnityEngine.UIElements.DisplayStyle.Flex
                            : UnityEngine.UIElements.DisplayStyle.None;
                };

            // Live content sync — poll the version endpoint; reload on change.
            if (SyncInterval > 0f)
            {
                _sync = new ContentSync(_assets.Loader)
                {
                    IntervalSeconds = SyncInterval,
                    // Reconcile once immediately after the long boot. Without this,
                    // an edit made after the chapter fetch but before ContentSync
                    // starts becomes the first baseline and is never hot-reloaded.
                    NotifyOnFirstPoll = true,
                };
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
                _shell.Hub.OnMenu = () => _shell.OpenSettingsAsync(); // avatar → account/settings
                _shell.Hub.OnStore = () => _shell.OpenPackShopAsync();   // currency "+" / Магазин → pack shop
                // Гардероб → the REAL, wallet-synced wardrobe for the game's main
                // heroine (title.hero ?? manifest.hero). Ownership lives in the
                // shared LvnWallet.Inventory, so it stays in sync with the in-story
                // wardrobe. (The prettier SkinShop screen gets wired to this same
                // data next.)
                _shell.Hub.OnWardrobe = () => OpenWardrobeFromHubAsync();
                _shell.Hub.OnGallery = OpenGalleryForRealAsync;
                _shell.Hub.OnProfile = () => _shell.OpenProfileAsync();
                _shell.Hub.OnDaily = () => _shell.OpenDailyAsync();
                _shell.Hub.PlayerName = _playerName;
                // Tapping a card opens the rich detail page seeded with this title.
                _shell.Hub.OnOpenDetail = t =>
                {
                    if (_shell.Detail != null)
                    {
                        _shell.Detail.TitleName = t?.name ?? t?.id ?? "";
                        var img = t?.card?.image ?? t?.cover_url;
                        if (!string.IsNullOrEmpty(img)) _shell.Detail.HeroImageUrl = img;
                        if (!string.IsNullOrEmpty(t?.card?.description)) _shell.Detail.Synopsis = t.card.description;
                        _shell.Detail.EnergyCost = t?.cost?.amount ?? 0;
                        // Real title behind the page → the Restart menu lists its
                        // actual chapters and reads/clears this title's progress.
                        _shell.Detail.Title = t;
                        _shell.Detail.OnResetProgress = ResetTitleProgressAsync;
                        _shell.Detail.Rebuild();
                    }
                    return _shell.OpenDetailAsync();
                };
            }

            // The FULL library warms in the background from here on: every
            // chapter of every title lands on disk while the player browses or
            // reads — the next chapter's loading screen is then near-instant,
            // and nothing EVER trickles in on camera. Yields to an active
            // chapter gate so it never steals that bandwidth.
            _ = WarmLibraryAsync(manifest, destroyCancellationToken);

            // The veil OWNS the whole app boot — one continuous surface from
            // the first frame to the first interactive screen. The shell's own
            // boot splash is suppressed (bootSplash: false): a second loading
            // screen under the veil would flash a second bar at the hand-off.
            // The veil walks 60→100% with the real boot-prefetch progress and
            // cross-fades into the menu.
            _ = DriveBootVeilAsync(prefetch, bootClock);

            var run = _shell.RunAsync(
                bootReady: () => prefetch.IsCompleted,
                chapterReady: BeginChapterLoading,
                chapterProgress: ch => ChapterLoadProgress,
                playChapter: PlayChapterAsync,
                askName: AskName,
                ct: destroyCancellationToken,
                bootSplash: false);
            await run;
        }

        // Walks the boot veil's last stretch (60→100%) with the real boot
        // prefetch, then cross-fades the veil into the first interactive screen.
        // Catch-all by design: this is fire-and-forget, and an exception here
        // would otherwise leave an opaque veil over the app forever.
        private async Task DriveBootVeilAsync(Task prefetch, System.Diagnostics.Stopwatch bootClock)
        {
            try
            {
                var l = _assets?.Loader;
                var ct = destroyCancellationToken;
                while (!prefetch.IsCompleted && !ct.IsCancellationRequested)
                {
                    float p = l != null && l.BatchTotal > 0
                        ? Mathf.Clamp01((float)l.BatchDone / l.BatchTotal) : 0f;
                    BootVeil.Progress(60 + Mathf.RoundToInt(p * 40f),
                        LvnNetworkStatus.IsOffline ? "нет сети — переподключение…" : "загрузка…");
                    await Task.Yield();
                }
                if (ct.IsCancellationRequested) return;
                BootVeil.Status("");
                await BootVeil.FadeOutAsync(0.4f);
                Debug.Log($"[lvn-boot] +{bootClock.ElapsedMilliseconds}ms veil handed off — app boot done");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                BootVeil.Hide();
            }
        }

        // ── chapter loading gate ────────────────────────────────────────────────
        // The shell's loading screen used to be decorative (ready = always true):
        // the script fetch, the state load and the first bg decode all happened
        // AFTER it faded — the player entered a black stage while the chapter
        // actually loaded. Now the screen kicks the real work and gates on it:
        // the script download plus the chapter's critical (required) assets via
        // the prioritized AssetScheduler; deferred assets keep streaming during
        // play. Offline every fetch fast-fails into the disk cache, so the gate
        // still completes — OfflinePolicy in PlayOneChapterAsync then decides
        // whether the chapter can actually play.
        private AssetScheduler _chapterSched;
        private Task _chapterScript = Task.CompletedTask;
        private LvnChapter _preparedChapter;

        private Func<bool> BeginChapterLoading(LvnChapter ch)
        {
            if (ch == null || _downloads == null) return () => true;
            _chapterScript = string.IsNullOrEmpty(ch.script_url)
                ? Task.CompletedTask
                : _assets.Loader.DownloadScriptCached(ch.script_url);
            _chapterSched = _downloads.BeginChapter(ch, destroyCancellationToken);
            _preparedChapter = ch;
            var script = _chapterScript;
            var sched = _chapterSched;
            _ = WatchChapterWarmAsync(ch, script, sched);
            // A faulted script task still completes the gate — PlayOneChapterAsync
            // owns the error path (cache fallback / "unavailable offline").
            return () => script.IsCompleted && sched.RequiredReady;
        }

        // Timing telemetry for the loading gate: how long the script fetch and
        // the required-asset warm ACTUALLY took (per-asset costs are the
        // [lvn-perf] lines) — the number to shrink when "loading feels long".
        private static async Task WatchChapterWarmAsync(LvnChapter ch, Task script, AssetScheduler sched)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { await script; } catch { /* the gate's error path reports it */ }
            Debug.Log($"[lvn-boot] warm {ch.id}: script +{sw.ElapsedMilliseconds}ms");
            while (!sched.RequiredReady && sw.ElapsedMilliseconds < 120_000)
                await Task.Delay(100);
            Debug.Log($"[lvn-boot] warm {ch.id}: required assets {sched.RequiredDone}/{sched.RequiredTotal} +{sw.ElapsedMilliseconds}ms");
        }

        // Progress for the loading bar: bytes when the manifest reports asset
        // sizes, else the required-count fraction (an empty/finished plan is 1).
        private float ChapterLoadProgress()
        {
            var s = _chapterSched;
            if (s == null || s.RequiredReady) return 1f;
            var p = s.Progress;
            if (p > 0f) return p;
            return s.RequiredTotal > 0 ? (float)s.RequiredDone / s.RequiredTotal : 0f;
        }

        // Charge a title's hub-entry cost (typically 1 energy for an expedition)
        // before it launches. Same store-retry flow as the per-chapter gate; free
        // when the title has no cost. Returns true if the player may enter.
        private async Task<bool> ChargeTitleEntryAsync(LvnTitle title)
        {
            var cost = title?.cost;
            if (cost == null || string.IsNullOrEmpty(cost.currency) || cost.amount <= 0) return true;
            // The entry was paid when the title was FIRST started — «Продолжить»
            // (or menu-exit + Play) must not charge the same entry again.
            if (LvnProgress.Reached(title) > 0) return true;

            string reason = "title:" + title.id;
            if (await Lvn.Services.LvnWallet.SpendAsync(cost.currency, cost.amount, reason)) return true;
            if (_shell == null) return false;

            var eco = _manifest?.economy;
            string title2 = eco?.gate_title ?? "Not enough energy";
            string msg = (eco?.gate_message ?? "You need more to start this.") + RefillHint(cost.currency);
            bool toStore = await _shell.ConfirmAsync(title2, msg,
                eco?.gate_buy ?? "Store", eco?.gate_cancel ?? "Not now");
            if (!toStore) return false;

            await _shell.OpenPackShopAsync();
            await Lvn.Services.LvnWallet.RefreshAsync();
            if (await Lvn.Services.LvnWallet.SpendAsync(cost.currency, cost.amount, reason)) return true;

            await _shell.AlertAsync(eco?.gate_denied ?? title2, msg);
            return false;
        }

        // Populate the CG gallery from every title's `gallery` (unlock state from
        // LvnGalleryStore), then open it. No items anywhere → the screen keeps its
        // built-in demo fallback.
        private Task OpenGalleryForRealAsync()
        {
            if (_shell?.Gallery != null && _manifest?.titles != null)
            {
                var entries = new System.Collections.Generic.List<CgGalleryScreen.Entry>();
                foreach (var t in _manifest.titles)
                    if (t?.gallery != null)
                        foreach (var g in t.gallery)
                            entries.Add(new CgGalleryScreen.Entry
                            {
                                Url = g.url,
                                Caption = g.name ?? g.id,
                                Unlocked = Lvn.UI.LvnGalleryStore.IsUnlocked(t.id, g.id),
                            });
                if (entries.Count > 0) _shell.Gallery.SetEntries(entries);
            }
            return _shell.OpenGalleryAsync();
        }

        // "⚡ +1 через 1 ч 20 мин" — the regen countdown for the gate popup, from the
        // wallet's computed refill state. Empty when the currency isn't regenerating.
        private static string RefillHint(string currency)
        {
            if (!Lvn.Services.LvnWallet.Regen.TryGetValue(currency, out var r) || r.NextRefillUnix <= 0) return "";
            long rem = r.NextRefillUnix - System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (rem <= 0) return "";
            long h = rem / 3600, m = (rem % 3600) / 60;
            return "\n\n⚡ +1 через " + (h > 0 ? h + " ч " + m + " мин" : m + " мин");
        }

        private async Task OpenStoreFromScriptAsync(Lvn.ILvnOpContext ctx)
        {
            try { await _shell.OpenPackShopAsync(); } // ONE store everywhere (the KR rule)
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
                // ONE wardrobe, one shell: mode=full historically opened a
                // separate fullscreen screen — it's gone; every path is the
                // sheet over the stage canvas now.
                await ShowStorySheetAsync(entity, onlySeen: false);
                _ = full; // accepted and ignored — deprecated authoring flag
            }
            finally { ctx.Resume(); }
        }

        // The ALWAYS-OPEN wardrobe (quick-menu «Гардероб»): the exact story-
        // moment experience — the hero on the current scene's background, the
        // same sheet — but the items are the player's COLLECTION: outfits the
        // story staged or offered along the way, plus everything bought.
        private async Task OpenWardrobeFromMenuAsync(VnStage stage)
        {
            var entity = ResolveMenuWardrobeEntity(stage);
            if (string.IsNullOrEmpty(entity)) return; // no dressable cast — nothing to open
            stage.CloseQuickMenu();
            // The story holds only because nothing advances it — block taps for
            // the sheet's whole life (a story-opened sheet gets this from Hold()).
            stage.InputBlocked = true;
            try
            {
                await ShowStorySheetAsync(entity,
                    onlySeen: _manifest?.ui?.wardrobe?.collection_only ?? true,
                    roster: BuildWardrobeRoster(entity));
            }
            finally { stage.InputBlocked = false; }
        }

        // The character pills of the always-open wardrobe: every dressable
        // entity, alias entities of the SAME character collapsed (they share
        // the exact set of story vars — Katya/cold_main/Главный_герой are one
        // heroine). The primary (resolved) character leads.
        private List<(string id, string name)> BuildWardrobeRoster(string primary)
        {
            var sprites = _manifest?.sprites;
            if (sprites == null) return null;
            var list = new List<(string id, string name)>();
            var sigs = new HashSet<string>();
            void TryAdd(string id)
            {
                if (string.IsNullOrEmpty(id) || !sprites.TryGetValue(id, out var d)
                    || d?.wardrobe == null || d.wardrobe.Count == 0) return;
                var vars = new List<string>();
                foreach (var kv in d.wardrobe)
                    if (!string.IsNullOrEmpty(kv.Value?.storyVar)) vars.Add(kv.Value.storyVar);
                vars.Sort();
                var sig = vars.Count > 0 ? string.Join("|", vars) : "id:" + id;
                if (!sigs.Add(sig)) return; // same character under another entity id
                list.Add((id, string.IsNullOrEmpty(d.name) ? id : d.name));
            }
            TryAdd(primary);
            foreach (var id in sprites.Keys) TryAdd(id);
            // The protagonist's pill wears the name the PLAYER chose, not the
            // import's internal label (Katya/cold_main/Главный_герой are all her).
            if (list.Count > 0 && list[0].id == primary && !string.IsNullOrEmpty(_playerName))
                list[0] = (list[0].id, _playerName);
            return list;
        }

        private static bool IsFirstChapter(LvnTitle title, LvnChapter chapter)
        {
            if (title?.seasons == null || chapter == null) return false;
            foreach (var se in title.seasons)
                if (se?.chapters != null)
                    foreach (var c in se.chapters)
                        if (c != null)
                            return c.id == chapter.id; // the manifest's first listed chapter
            return false;
        }

        private bool AnyWardrobeEntity()
        {
            var sprites = _manifest?.sprites;
            if (sprites == null) return false;
            foreach (var kv in sprites)
                if (kv.Value?.wardrobe != null && kv.Value.wardrobe.Count > 0) return true;
            return false;
        }

        // The HUB wardrobe: same sheet, same canvas — the hub cross-fades away,
        // the stage dresses itself with the last scene the player saw (or the
        // engine's dark), the hero steps on, the sheet slides up. Closing plays
        // it all back. ONE wardrobe everywhere; the old fullscreen screen died.
        private async Task OpenWardrobeFromHubAsync()
        {
            var stage = Stage;
            if (stage == null) return;
            var entity = ResolveMenuWardrobeEntity(stage);
            if (string.IsNullOrEmpty(entity)) return;
            var hub = _shell?.Hub;
            if (hub != null)
            {
                await ScreenFx.FadeAsync(hub, 1f, 0f, 0.25f, destroyCancellationToken);
                hub.style.display = DisplayStyle.None;
            }
            try
            {
                var bg = Lvn.UI.VnStage.LastSceneBgUrl;
                if (!string.IsNullOrEmpty(bg))
                    stage.ApplyStage(new Newtonsoft.Json.Linq.JObject
                    { ["op"] = "bg", ["sprite_url"] = bg });
                await ShowStorySheetAsync(entity,
                    onlySeen: _manifest?.ui?.wardrobe?.collection_only ?? true,
                    roster: BuildWardrobeRoster(entity));
            }
            finally
            {
                if (hub != null)
                {
                    hub.style.display = DisplayStyle.Flex;
                    _ = ScreenFx.FadeAsync(hub, 0f, 1f, 0.25f, destroyCancellationToken);
                }
            }
        }

        // Who the menu wardrobe dresses: the configured hero, else the one on
        // stage whose wardrobe writes story vars (the imported protagonist),
        // else anyone sensible with a wardrobe.
        private string ResolveMenuWardrobeEntity(VnStage stage)
        {
            var sprites = _manifest?.sprites;
            if (sprites == null || sprites.Count == 0) return null;
            bool HasWardrobe(string id) => !string.IsNullOrEmpty(id)
                && sprites.TryGetValue(id, out var d) && d?.wardrobe != null && d.wardrobe.Count > 0;
            bool WritesStory(string id)
            {
                if (!sprites.TryGetValue(id, out var d) || d?.wardrobe == null) return false;
                foreach (var slot in d.wardrobe.Values)
                    if (!string.IsNullOrEmpty(slot?.storyVar)) return true;
                return false;
            }

            var cfg = _manifest?.ui?.wardrobe;
            if (HasWardrobe(cfg?.entity)) return cfg.entity;
            var onStage = stage != null ? stage.ActorsOnStage() : new List<string>();
            foreach (var id in onStage) if (HasWardrobe(id) && WritesStory(id)) return id;
            foreach (var id in sprites.Keys) if (HasWardrobe(id) && WritesStory(id)) return id;
            foreach (var id in onStage) if (HasWardrobe(id)) return id;
            foreach (var id in sprites.Keys) if (HasWardrobe(id)) return id;
            return null;
        }

        private async Task ShowStorySheetAsync(string entity, bool onlySeen,
            List<(string id, string name)> roster = null)
        {
            if (_storySheet == null)
            {
                var ui = _manifest?.ui ?? new LvnUiConfig();
                _storySheet = new WardrobeSheet(ui.wardrobe, ui.dialogue, ui.choices, _assets);
                _storySheet.SetManifest(_manifest);
                _storySheet.OpenStore = () => _shell.OpenPackShopAsync();
                _storySheet.ConfirmTopUp = (title, msg) => _shell.ConfirmAsync(title, msg, "Store", "Not now");
                _storySheet.Alert = (title, msg) => _shell.AlertAsync(title, msg);
                // Write the player's wardrobe pick back into the novel's story state
                // (nested, like the script's own `set`), then re-dress the actor
                // against the new value. Order matters: SetVar BEFORE RefreshActor so
                // the {Wardrobe.*} axes re-interpolate against the fresh var.
                _storySheet.OnEquip = (ent, storyVar, value) =>
                {
                    var p = Stage?.Player;
                    if (p == null || string.IsNullOrEmpty(storyVar)) return;
                    Newtonsoft.Json.Linq.JToken jv =
                        long.TryParse(value, out var n) ? new Newtonsoft.Json.Linq.JValue(n)
                        : double.TryParse(value, out var d) ? new Newtonsoft.Json.Linq.JValue(d)
                        : (Newtonsoft.Json.Linq.JToken)new Newtonsoft.Json.Linq.JValue(value);
                    p.SetVar(storyVar, jv);
                    Stage.RefreshActor(ent);
                };
            }
            _storySheet.OnlySeen = onlySeen; // shared instance — set on EVERY open
            _storySheet.SetRoster(roster);
            // Platform back while the sheet is up = the sheet's own cancel.
            var st0 = Stage;
            if (st0 != null) st0.PanelCancelRequested = () => _storySheet?.Hide();
            var st = Stage;
            // Who stood on stage BEFORE the fitting — everyone staged purely to
            // be dressed (the opener, every pill switch) leaves again at close.
            var wasOn = new HashSet<string>(st != null ? st.ActorsOnStage() : new List<string>());
            _storySheet.OnCharacterPicked = (from, to) =>
            {
                if (st == null) return;
                if (!wasOn.Contains(from)) st.HideActor(from);
                st.EnsureActorShown(to);
            };
            // The sheet mirrors the LIVE actor — make sure the active hero is on
            // stage so the wardrobe always shows who you're dressing, even when the
            // story beat opened it on an empty stage.
            st?.EnsureActorShown(entity);
            SeedWardrobeFromStoryVars(entity);
            var done = _storySheet.ShowAsync(entity);   // logic only — the host animates
            await st.ShowPanelAsync(_storySheet);       // dialogue fades, frame slides up
            try { await done; }
            finally
            {
                await st.HidePanelAsync();              // frame slides away, dialogue returns
                var cur = _storySheet.CurrentEntity ?? entity;
                if (!wasOn.Contains(cur)) st.HideActor(cur);
            }
        }

        // The in-story sheet decides "what's worn" from LvnWardrobe's OWN equip
        // registry (session state / restored save) — it has no idea the story's
        // {Wardrobe.*} var might already hold a different value (the chapter's
        // own default `set`, or a scene-forced costume change). Left unsynced,
        // BuildFor sees no match for that axis and jumps the preview to the
        // list's first item — a visible flash to a random outfit right as the
        // sheet opens. Sync every axis with a storyVar from the CURRENT var
        // value first, so the sheet's own initial pick already matches the
        // actor standing on stage.
        private void SeedWardrobeFromStoryVars(string entity)
        {
            var p = Stage?.Player;
            var wardrobe = _manifest?.sprites != null && _manifest.sprites.TryGetValue(entity, out var def)
                ? def?.wardrobe : null;
            if (p == null || wardrobe == null) return;
            foreach (var kv in wardrobe)
            {
                var storyVar = kv.Value?.storyVar;
                if (string.IsNullOrEmpty(storyVar)) continue;
                var v = p.GetVar(storyVar);
                if (v == null) continue;
                Lvn.UI.LvnWardrobe.Equip(entity, kv.Key, v.ToString());
            }
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
            // Shared panel (see NovelShell.InitDocument) — the stage document
            // layers below the shell (10 < 30) inside the same panel.
            LvnPanel.SetTheme(ShellTheme);
            doc.panelSettings = LvnPanel.Shared;
            doc.sortingOrder = 10;
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
            // The manifest is the boot's single point of truth — a fresh install
            // has nothing without it. One transient failure (flaky emulator NAT,
            // a mid-handshake reset) must not fall through to "no manifest":
            // three quick attempts before the caller's slower recovery paths.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var json = await _assets.Loader.DownloadScriptText("/v1/content/manifest", default, singleAttempt: true);
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json) ?? new LvnManifest();
                }
                catch (Exception ex) when (attempt < 3)
                {
                    Debug.LogWarning($"[novelapp] manifest fetch attempt {attempt} failed: {ex.Message} — retrying");
                    await Task.Delay(700 * attempt);
                }
            }
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
            // Computed BEFORE any SetCurrent: a novel-fresh start (first ever
            // play, or a post-finale replay) re-asks the player's name inside
            // the first chapter's entry.
            bool novelFreshStart = resume == null;
            if (resume != null) chapter = resume;
            // A COMPLETED novel replays clean: Current is cleared on the finale
            // but the title-scope vars still hold the whole playthrough — route
            // the fresh entry through the restart machinery so chapter one seeds
            // from its pristine entry checkpoint, not from endgame stats.
            if (resume == null && LvnProgress.Reached(title) > 0 && chapter != null)
                LvnProgress.RequestRestart(title?.id, chapter.id);
            // Resuming a chapter the player already paid to enter must not charge
            // again. "Already entered" = ITS autosave exists (written at entry) —
            // the progress marker alone isn't enough: finishing a chapter moves
            // the marker to the NEXT one, and that entry hasn't been paid yet.
            var entrySlot = LvnSaveStore.Get(title?.id, LvnSaveStore.AutoSlot);
            bool alreadyEntered = resume != null && entrySlot?.Snap != null
                && entrySlot.Snap.ScriptUrl == resume.script_url
                && !entrySlot.Snap.Finished;
            while (chapter != null)
            {
                // The script must be REACHABLE before anything is charged — an
                // offline entry used to burn the energy and silently bounce to
                // the menu (and charge AGAIN on the retry).
                if (!await EnsureChapterScriptAsync(chapter))
                {
                    var eco = _manifest?.economy;
                    await _shell.AlertAsync(eco?.gate_title ?? "Нет соединения",
                        "Глава недоступна без сети. Проверь подключение и попробуй ещё раз.");
                    break;
                }
                if (!alreadyEntered && !await ChargeChapterEntryAsync(chapter))
                    break; // couldn't/wouldn't pay the entry cost → back to the carousel
                alreadyEntered = false;
                // Stream this chapter's asset plan. The FIRST chapter's plan was
                // started under the loading screen (BeginChapterLoading); a resume
                // into a later chapter, or a seamless next chapter, starts its own
                // here — critical assets first, deferred during play.
                if (_downloads != null && !ReferenceEquals(chapter, _preparedChapter))
                    _chapterSched = _downloads.BeginChapter(chapter, destroyCancellationToken);
                _preparedChapter = null;
                LvnProgress.SetCurrent(title, chapter);
                SyncProgressVault(); // every progress move lands in all three homes
                ChapterStarted?.Invoke(title, chapter);
                Lvn.Services.LvnAnalytics.Track("chapter_start",
                    ("title", title?.id), ("chapter", chapter.id));
                var finished = await PlayOneChapterAsync(title, chapter, playerName, novelFreshStart);
                novelFreshStart = false; // only the entry chapter of this run counts
                if (finished == null) break; // left mid-chapter (cancel/error) → carousel
                ChapterFinished?.Invoke(title, finished);
                Lvn.Services.LvnAnalytics.Track("chapter_finish",
                    ("title", title?.id), ("chapter", finished.id));
                // A cross-chapter save load can land the player in another title —
                // continue along whichever title the finished chapter belongs to.
                var (owner, _) = FindChapterByScriptUrl(finished.script_url);
                if (owner != null) title = owner;
                var next = NextChapterOf(title, finished);
                // The FINISH is what advances progress — not the «Дальше» tap.
                // Leaving via the chapter-end menu used to strand the marker on
                // the finished chapter, and «Играть» replayed it from the top.
                if (next != null)
                    LvnProgress.SetCurrent(title, next);
                else
                    LvnProgress.ClearCurrent(title); // the novel is complete — replays restart
                SyncProgressVault();
                // Between-chapters screen (ui.chapter_end): "Конец главы" with
                // continue/menu. Without it chapters flow seamlessly, as before.
                if (_shell?.ChapterEnd != null)
                {
                    bool goNext = await _shell.ChapterEnd.ShowAsync(finished.name, hasNext: next != null);
                    if (!goNext || next == null) break;
                }
                else if (next == null) break;
                chapter = next;
            }
            // Back to the menu — stop the chapter scheduler so its deferred
            // downloads don't keep competing with the menu's own refresh.
            _downloads?.EndChapter();
            _chapterSched = null;
            // A chapter's worth of remote sprites fragments the panel's dynamic
            // atlas (freed regions rarely fit the next tenant); rebuild it clean
            // at this natural boundary.
            try
            {
                var panel = Stage != null
                    ? Stage.GetComponent<UIDocument>()?.rootVisualElement?.panel : null;
                if (panel != null) RuntimePanelUtils.ResetDynamicAtlas(panel);
            }
            catch { /* atlas reset is an optimization, never a failure */ }
        }

        // Preflight: make the chapter's script locally available (cache hit or
        // a live fetch) BEFORE the entry charge — money never burns on a
        // chapter that can't start. The later fetch inside PlayOneChapterAsync
        // then hits the cache.
        private async Task<bool> EnsureChapterScriptAsync(LvnChapter chapter)
        {
            if (chapter == null || string.IsNullOrEmpty(chapter.script_url)) return false;
            if (_assets.Loader.IsScriptCached(chapter.script_url)) return true;
            try
            {
                var json = await _assets.Loader.DownloadScriptCached(chapter.script_url);
                return !string.IsNullOrEmpty(json);
            }
            catch { return false; }
        }

        // Background full-library warm: чей-то экран загрузки всегда важнее —
        // the loop parks while a chapter scheduler is actively gating.
        private async Task WarmLibraryAsync(LvnManifest manifest, System.Threading.CancellationToken ct)
        {
            try
            {
                await Task.Delay(3000, ct); // let the boot/menu settle first
                int warmed = 0, skipped = 0;
                if (manifest?.titles != null)
                    foreach (var t in manifest.titles)
                    {
                        if (t?.seasons == null) continue;
                        foreach (var se in t.seasons)
                        {
                            if (se?.chapters == null) continue;
                            foreach (var ch in se.chapters)
                            {
                                if (ch == null) continue;
                                if (!string.IsNullOrEmpty(ch.script_url) && !_assets.Loader.IsScriptCached(ch.script_url))
                                    try { await _assets.Loader.DownloadScriptCached(ch.script_url); } catch { }
                                if (ch.assets == null) continue;
                                foreach (var kv in ch.assets)
                                {
                                    if (ct.IsCancellationRequested) return;
                                    var url = kv.Key;
                                    if (string.IsNullOrEmpty(url)) continue;
                                    // an active chapter gate owns the bandwidth
                                    while (_chapterSched != null && !_chapterSched.AllDone && !ct.IsCancellationRequested)
                                        await Task.Delay(500, ct);
                                    // …and so does anything a LIVE surface is
                                    // waiting to draw right now: an actor
                                    // mid-scene must never queue behind next
                                    // week's chapters.
                                    while (_assets.LivePressure > 0 && !ct.IsCancellationRequested)
                                        await Task.Delay(150, ct);
                                    if (Lvn.Content.LvnNetworkStatus.IsOffline)
                                    { await Task.Delay(3000, ct); continue; }
                                    if (_assets.Loader.IsAssetCached(url)) { skipped++; continue; }
                                    try { await _assets.Loader.DownloadAssetBytes(url, ct); warmed++; }
                                    catch (System.OperationCanceledException) { return; }
                                    catch { /* self-heal covers per-file failures */ }
                                }
                            }
                        }
                    }
                Debug.Log($"[lvn-warm] library fully cached ({warmed} fetched, {skipped} already local)");
            }
            catch (System.OperationCanceledException) { /* teardown */ }
        }

        // The debug faucet's grant: credit the wallet (EarnAsync fires
        // LvnWallet.Changed — the shell's HUD pill updates itself) and
        // reconcile with the server so the balance survives restarts.
        private async Task GrantFaucetAsync(string currency, int amount)
        {
            await Lvn.Services.LvnWallet.EarnAsync(currency, amount, "debug_faucet");
            await Lvn.Services.LvnWallet.RefreshAsync();
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
            string msg = (eco.gate_message ?? "You need more to open this chapter.") + RefillHint(currency);
            bool toStore = await _shell.ConfirmAsync(title, msg,
                eco.gate_buy ?? "Store", eco.gate_cancel ?? "Not now");
            if (!toStore) return false;

            await _shell.OpenPackShopAsync();
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
        private async Task<LvnChapter> PlayOneChapterAsync(LvnTitle title, LvnChapter chapter, string playerName, bool novelFreshStart = false)
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
                // Local first: a kill during the network sync must not leave the
                // old autosave alive with the restart flag already consumed.
                LvnSaveStore.Delete(title?.id, LvnSaveStore.AutoSlot);
                var curGlobal = await _state.LoadVarsAsync(GlobalScopeId, default);
                if (curGlobal != null && curGlobal.Count > 0) Stage.SeedVars[GlobalVar] = curGlobal;
                await SaveScopedVarsAsync(title?.id, Stage.SeedVars);
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

            // Device-side wardrobe equips (the hub sheet has no live Player to
            // write through) land in the story vars HERE: every wardrobe slot
            // bound to a story var seeds the equipped value on a fresh entry, so
            // template-driven axes ({Wardrobe.mainCh_Clothes}) show the outfit
            // the player picked between sessions. Resumes keep the snapshot's
            // own state — the story's mid-chapter forces stay authoritative.
            if (!resuming && _manifest?.sprites != null && Stage.SeedVars != null)
            {
                foreach (var kv in _manifest.sprites)
                {
                    var wb = kv.Value?.wardrobe;
                    if (wb == null) continue;
                    var worn = Lvn.UI.LvnWardrobe.Equipped(kv.Key);
                    if (worn.Count == 0) continue;
                    foreach (var slot in wb)
                        if (!string.IsNullOrEmpty(slot.Value?.storyVar)
                            && worn.TryGetValue(slot.Key, out var val) && !string.IsNullOrEmpty(val))
                            SetVarPath(Stage.SeedVars, slot.Value.storyVar, val);
                }
            }

            // A FRESH entry (chapter transition, picker restart, first launch) is
            // the moment the entry checkpoint captures; a mid-chapter resume must
            // NOT overwrite it — and neither may a REPLAY: the checkpoint is the
            // vars of the FIRST entry ever, the restart anchor. Overwriting it
            // with a later playthrough's stats would corrupt restarts forever.
            if (!resuming && LvnProgress.Checkpoint(title?.id, chapter.id) == null)
                LvnProgress.SaveCheckpoint(title?.id, chapter.id, Stage.SeedVars);

            Stage.SetSaveContext(title?.id, chapter.id, chapter.script_url);
            Stage.Gallery = title?.gallery;
            // The first line holds until the entry choreography (loader reveal,
            // plus the chapter-title card on fresh entries) finishes — the stage
            // dresses itself silently underneath. A RESUME holds too (it skips
            // only the title card): without the gate the first line typed — with
            // its keystroke sound — under the still-opaque loader, and the reveal
            // faded into a scene already mid-sentence.
            var entryDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Stage.EntryGate = entryDone.Task;
            Stage.Play(json, warmIntroSpine: !resuming); // resume restores below — don't run/warm the intro
            if (Stage.Player != null && !string.IsNullOrEmpty(playerName))
                Stage.Player.Vars["player"] = playerName;

            // Title-level variable declarations (title.vars_url): ONE block per
            // game instead of a 250-op boilerplate at the top of every chapter.
            // Fresh entry: chapter-scoped keys reset to their defaults; then both
            // scopes fill only what is still unset (progress always wins).
            var titleVars = await LoadTitleVarsAsync(title);
            if (titleVars != null && Stage.Player != null && !resuming)
            {
                Stage.Player.ResetScope((titleVars.chapter ?? new Newtonsoft.Json.Linq.JObject())
                    .Properties().Select(p => p.Name).ToList());
                Stage.Player.ApplyDefaults(titleVars.game);
                Stage.Player.ApplyDefaults(titleVars.chapter);
            }

            if (!resuming)
            {
                // The entry IS the purchase receipt: write the autosave NOW, so a
                // crash in the first lines never re-charges this chapter (and the
                // player lands back at its top, not at the carousel).
                Stage.AutosaveNow();
            }
            if (resuming)
            {
                Debug.Log($"[novelapp] resuming '{chapter.id}' from autosave (@{autosave.Snap.Index})");
                // The snapshot carries the GLOBAL stats as they were at save time —
                // another novel may have moved them since. Load the live ones FIRST:
                // the overlay below then runs before any of the restore's async
                // continuations, so the resumed beat's conditions read fresh stats.
                var freshGlobal = await _state.LoadVarsAsync(GlobalScopeId, default);
                Stage.RestoreSnapshot(autosave.Snap);
                if (Stage.Player != null && !string.IsNullOrEmpty(playerName))
                    Stage.Player.Vars["player"] = playerName;
                if (freshGlobal != null && freshGlobal.Count > 0 && Stage.Player != null)
                    Stage.Player.Vars[GlobalVar] = freshGlobal;
                // A resume keeps the snapshot's own state; the declaration only
                // fills keys the snapshot never had (e.g. vars added after the
                // save was written) — never resets chapter scope mid-chapter.
                if (titleVars != null && Stage.Player != null)
                {
                    Stage.Player.ApplyDefaults(titleVars.game);
                    Stage.Player.ApplyDefaults(titleVars.chapter);
                }
            }

            // Liminal-style entry: the chapter has been booting BEHIND the opaque
            // loader; once the first background lands (or a short grace passes —
            // some scenes are text-only), fade the loader into the LIVE scene and
            // float the chapter title over it. A resume skips the title card (the
            // player is mid-scene, not at the opening). Chapter 2+ in a seamless
            // chain: the loader is already hidden (no-op), the title still shows.
            float revealStart = Time.realtimeSinceStartup;
            float revealDeadline = revealStart + (_shell?.Transitions?.backdrop_grace ?? 2f);
            while (Stage != null && !Stage.HasBackdrop && Time.realtimeSinceStartup < revealDeadline)
                await Task.Yield();
            Debug.Log($"[novelapp] entry reveal: backdrop={Stage?.HasBackdrop} " +
                      $"waited={(Time.realtimeSinceStartup - revealStart) * 1000f:F0}ms resuming={resuming}");
            try
            {
                if (_shell != null)
                {
                    await _shell.RevealFromLoadingAsync();
                    if (!resuming) await _shell.ShowChapterTitleAsync(chapter, title);
                    // The NOVEL asks the name here — after the title card, the
                    // live scene as the backdrop, just the panel at the bottom.
                    // Manifest switch ui.name_input.enabled; fresh starts of the
                    // first chapter only (restart or first-ever), prefilled.
                    var ni = _manifest?.ui?.name_input;
                    bool firstChapter = IsFirstChapter(title, chapter);
                    if (AskName && ni != null && (ni.enabled ?? true)
                        && !resuming && firstChapter
                        && (restart || novelFreshStart || string.IsNullOrEmpty(_playerName))
                        && _shell.NameInput != null)
                    {
                        try
                        {
                            var entered = await _shell.NameInput.AskAsync(
                                null, _playerName, overlay: true, destroyCancellationToken);
                            if (!string.IsNullOrEmpty(entered))
                            {
                                _playerName = entered;
                                Lvn.UI.LvnPrefs.PlayerName = entered;
                                if (Stage.Player != null) Stage.Player.Vars["player"] = entered;
                            }
                        }
                        catch (OperationCanceledException) { /* teardown mid-ask */ }
                    }
                }
            }
            finally { entryDone.TrySetResult(true); } // release the first line NO MATTER WHAT

            // Drive the HUD percent until the chapter ends — or the player asks
            // out (the quick menu's Exit; position already autosaved, so the
            // carousel's Continue leads straight back to this line).
            // Task.Yield can't throw — the real exit-on-teardown is the token
            // check (a destroyed host must not keep a zombie progress loop).
            while (Stage.Player != null && !Stage.Player.Finished && !Stage.ExitRequested
                   && !destroyCancellationToken.IsCancellationRequested)
            {
                _shell.Hud.SetProgress(Stage.Player.ProgressIndex, Stage.Player.Count);
                await Task.Yield();
            }
            bool exited = Stage.ExitRequested;
            Stage.ClearExitRequest();
            if (exited) Stage.ClearStage(); // leave nothing behind under the carousel
            // Persist the chapter's ending state so the next chapter (and the next
            // session) resume with the same stats — whether it finished or the player
            // left mid-chapter (the loop also breaks on cancellation).
            // The owner may have CHANGED under us (a cross-title save load) —
            // the finished chapter's vars belong to the title actually playing.
            var ownerId = _currentTitle?.id ?? title?.id;
            if (Stage.Player != null) await SaveScopedVarsAsync(ownerId, VarsToJObject(Stage.Player.Vars));
            _shell.Hud.SetProgress(1, 1);
            // The chapter that actually played to the end — a cross-chapter save
            // load may have switched the stage away from the requested one.
            bool finished = Stage.Player != null && Stage.Player.Finished;
            var played = _currentChapter ?? chapter;
            _currentChapter = null;
            _currentTitle = null;
            // Free the finished chapter's decoded art (a chapter can hold dozens of
            // full-res RGBA sprites). Anything the MENU still shows is pinned:
            // covers and loading backdrops often reuse in-chapter bg files, and
            // destroying a sprite the carousel still references leaves white
            // cards. The disk cache is intact so the next entry re-decodes fast.
            var pinned = MenuArtUrls();
            _assets.Loader.UnloadWhere(u =>
                (u.Contains("/art/") || u.Contains("/bg/"))
                && !pinned.Contains(u.Replace("@2k", ""))); // pins hold ORIGINAL urls; cache may hold @2k variants
            return finished ? played : null;
        }

        // Every image url the MENU surfaces reference (covers, chapter loading
        // backdrops, collection art) — the chapter-end unload must never destroy
        // these while the carousel/hub still draw them. Rebuilt lazily per
        // manifest (content live-reload swaps the manifest object).
        private HashSet<string> _menuArt;
        private LvnManifest _menuArtFor;

        private HashSet<string> MenuArtUrls()
        {
            if (_menuArt != null && ReferenceEquals(_menuArtFor, _manifest)) return _menuArt;
            var set = new HashSet<string>();
            void Take(string u) { if (!string.IsNullOrEmpty(u)) set.Add(u); }
            if (_manifest?.titles != null)
                foreach (var t in _manifest.titles)
                {
                    if (t == null) continue;
                    Take(t.cover_url);
                    Take(t.card?.image); // detail-screen hero art
                    if (t.seasons == null) continue;
                    foreach (var s in t.seasons)
                    {
                        if (s?.chapters == null) continue;
                        foreach (var c in s.chapters) Take(c?.bg_url);
                    }
                }
            if (_manifest?.collections != null)
                foreach (var col in _manifest.collections)
                    Take(col?.card?.image);
            _menuArt = set;
            _menuArtFor = _manifest;
            return set;
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
            Stage.EntryGate = null; // a save-load lands mid-scene — no entry choreography
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

        // "Restart the whole expedition": wipe this title's persisted stats and
        // drop every save slot, then clear its reading progress/checkpoints so the
        // next play starts from chapter one, clean. The cross-novel `global` stats
        // are LEFT intact — they belong to the player, not this one expedition.
        // Wired into TitleDetailScreen.OnResetProgress.
        private async Task ResetTitleProgressAsync(LvnTitle title)
        {
            if (title == null) return;
            // LOCAL state first — a kill mid-network-await must not leave a
            // "continue" that resumes the middle of the novel with zeroed stats.
            foreach (var slot in new System.Collections.Generic.List<string>(LvnSaveStore.Slots(title.id).Keys))
                LvnSaveStore.Delete(title.id, slot);
            LvnProgress.ResetTitle(title.id);
            try { await _state.SaveVarsAsync(title.id, new Newtonsoft.Json.Linq.JObject(), default); }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] stat wipe failed: {ex.Message}"); }
            Debug.Log($"[novelapp] restarted expedition '{title.id}' — stats & saves cleared");
            SyncProgressVault(); // the wipe is progress too — all homes agree
        }

        // Write a dotted path ("Wardrobe.mainCh_Clothes") into a seed JObject,
        // creating intermediate objects — mirrors the player's SetVar nesting.
        // Numeric strings store as numbers so conditions compare numerically.
        private static void SetVarPath(Newtonsoft.Json.Linq.JObject vars, string key, string value)
        {
            Newtonsoft.Json.Linq.JToken jv =
                long.TryParse(value, out var n) ? new Newtonsoft.Json.Linq.JValue(n)
                : double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)
                    ? new Newtonsoft.Json.Linq.JValue(d)
                    : (Newtonsoft.Json.Linq.JToken)new Newtonsoft.Json.Linq.JValue(value);
            var parts = key.Split('.');
            var cur = vars;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!(cur[parts[i]] is Newtonsoft.Json.Linq.JObject next))
                {
                    next = new Newtonsoft.Json.Linq.JObject();
                    cur[parts[i]] = next;
                }
                cur = next;
            }
            cur[parts[parts.Length - 1]] = jv;
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
            // Double-homed identity: PlayerPrefs AND a plain file. The id is the
            // key to every server-side possession (wallet, stats, progress
            // backup) — a corrupted prefs blob must never orphan them.
            var idFile = System.IO.Path.Combine(Application.persistentDataPath, "lvn_user.id");
            var id = PlayerPrefs.GetString("lvn_user", "");
            if (string.IsNullOrEmpty(id))
            {
                try { if (System.IO.File.Exists(idFile)) id = System.IO.File.ReadAllText(idFile).Trim(); }
                catch { /* unreadable second home — fall through */ }
            }
            if (string.IsNullOrEmpty(id)) id = System.Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString("lvn_user", id);
            PlayerPrefs.Save();
            try { System.IO.File.WriteAllText(idFile, id); } catch { /* prefs copy still holds */ }
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
        // Desktop/editor: closing the window must save exactly like a mobile
        // background — otherwise the last lines and unsynced vars are lost.
        private void OnApplicationQuit() => OnApplicationPause(true);

        // The vault sync: collect the bundle, write the atomic file home and
        // push the server backup (offline-first store queues it when offline).
        private void SyncProgressVault()
        {
            if (_manifest == null) return;
            try
            {
                var bundle = ProgressVault.Collect(_manifest);
                ProgressVault.WriteLocal(bundle);
                if (_state != null) _ = _state.SaveVarsAsync(ProgressVault.Scope, bundle, default);
            }
            catch (Exception e) { Debug.LogWarning("[vault] sync failed: " + e.Message); }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused && _state != null && Stage?.Player != null && _currentTitle != null)
                _ = SaveScopedVarsAsync(_currentTitle.id, VarsToJObject(Stage.Player.Vars));
            if (paused) SyncProgressVault();
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
            // The veil is a root GameObject (it outlives this component by
            // design during boot) — a host tearing NovelApp down mid-boot must
            // not be left with an opaque, input-eating veil over its own UI.
            BootVeil.Hide();
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
