using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The full novel shell — the loop that ties the manifest-driven screens
    /// together: <b>boot splash → title carousel → (name input) → chapter loading
    /// → title card → play → back to the carousel</b>. Build it on a
    /// <see cref="UIDocument"/>, hand it an <see cref="LvnManifest"/> + an
    /// <see cref="ILvnAssets"/>, and a <c>playChapter</c> delegate that runs the
    /// actual chapter (e.g. drives a <c>VnStage</c>) and returns when it ends.
    /// Everything visual is themed from <c>manifest.ui</c>.
    /// </summary>
    public sealed class NovelShell : MonoBehaviour
    {
        public BootScreen Boot { get; private set; }
        public TitleCarousel Carousel { get; private set; }
        /// <summary>The hub browse flow (collections → cards → detail), used when
        /// <c>ui.browse.layout == "hub"</c> instead of the carousel.</summary>
        public BrowseHub Hub { get; private set; }
        public NameInputScreen NameInput { get; private set; }
        public LoadingScreen Loading { get; private set; }
        public TitleCard Title { get; private set; }
        public GameHud Hud { get; private set; }

        /// <summary>ui.hud.mode == "choices": the HUD hides during plain reading
        /// and only surfaces while a choice is up (the host wires the stage event).</summary>
        public bool HudChoicesOnly { get; private set; }

        /// <summary>The between-chapters screen — null unless ui.chapter_end is
        /// configured (the chapter loop checks before pausing on it).</summary>
        public ChapterEndScreen ChapterEnd { get; private set; }
        /// <summary>The boot auth screen; null unless manifest ui.auth enables it.</summary>
        public AuthScreen Auth { get; private set; }
        /// <summary>The currency store overlay (open via <see cref="OpenStoreAsync"/>).</summary>
        public StoreScreen Store { get; private set; }
        /// <summary>The app-level settings overlay (open via <see cref="OpenSettingsAsync"/>).</summary>
        public SettingsScreen Settings { get; private set; }
        /// <summary>The rich title-detail page (chapters, saves, stats, play).</summary>
        public TitleDetailScreen Detail { get; private set; }
        /// <summary>The CG art gallery.</summary>
        public CgGalleryScreen Gallery { get; private set; }
        /// <summary>The player profile page.</summary>
        public ProfileScreen Profile { get; private set; }
        /// <summary>The daily-rewards calendar.</summary>
        public DailyRewardsScreen Daily { get; private set; }
        /// <summary>The wardrobe / skin shop.</summary>
        public SkinShopScreen SkinShop { get; private set; }
        /// <summary>The currency-pack shop.</summary>
        public PackShopScreen PackShop { get; private set; }
        /// <summary>The universal modal popup (alerts/confirms), topmost overlay.</summary>
        public PopupScreen Popup { get; private set; }
        /// <summary>The wardrobe overlay (open via <see cref="OpenWardrobeAsync"/>).</summary>
        public WardrobeScreen Wardrobe { get; private set; }
        /// <summary>The in-story wardrobe bottom sheet — dresses the live actor
        /// (open via <see cref="OpenWardrobeSheetAsync"/>).</summary>
        public WardrobeSheet WardrobeStory { get; private set; }

        private UIDocument _doc;
        private VisualElement _root;
        private LvnManifest _manifest;
        private ILvnAssets _assets;
        private string _playerName;

        /// <summary>The shell's UIDocument. Assign
        /// <c>Document.panelSettings.themeStyleSheet</c> a runtime theme so the
        /// screens' text has a font (UI Toolkit renders no text without one).</summary>
        public UIDocument Document => _doc;

        /// <summary>Create a shell on a fresh GameObject with its own UIDocument.
        /// Pass a <paramref name="theme"/> (a runtime ThemeStyleSheet) so text
        /// renders — without one UI Toolkit draws shapes but no glyphs.</summary>
        public static NovelShell Create(Transform parent = null, int sortingOrder = 30, ThemeStyleSheet theme = null)
        {
            var go = new GameObject("NovelShell", typeof(NovelShell));
            if (parent != null) go.transform.SetParent(parent, false);
            var shell = go.GetComponent<NovelShell>();
            shell.InitDocument(sortingOrder, theme);
            return shell;
        }

        private void InitDocument(int sortingOrder, ThemeStyleSheet theme = null)
        {
            // ONE shared PanelSettings across the whole app (stage + shell): a
            // single panel keeps focus/navigation working across documents and
            // shares one dynamic atlas. Layering within the panel is per-document
            // sortingOrder; the panel itself sits above the world-stage canvas.
            LvnPanel.SetTheme(theme);
            _doc = gameObject.GetComponent<UIDocument>();
            if (_doc == null) _doc = gameObject.AddComponent<UIDocument>();
            _doc.panelSettings = LvnPanel.Shared;
            _doc.sortingOrder = sortingOrder;
        }

        /// <summary>Build the screen tree from the manifest. Idempotent.</summary>
        public void Build(LvnManifest manifest, ILvnAssets assets)
        {
            _manifest = manifest ?? new LvnManifest();
            _assets = assets;
            var ui = _manifest.ui ?? new LvnUiConfig();
            Transitions = ui.transitions;

            if (_doc == null) InitDocument(30);
            _root = _doc.rootVisualElement;
            _root.Clear();
            _root.style.flexGrow = 1;

            Boot = new BootScreen(ui.boot, assets); Boot.Hide(); Add(Boot);
            Carousel = new TitleCarousel(_manifest.titles, ui.carousel, assets); Hide(Carousel); Add(Carousel);
            Hub = new BrowseHub(ui.browse, assets); Hub.SetData(_manifest.collections, _manifest.titles);
            Hide(Hub); Add(Hub);
            NameInput = new NameInputScreen(ui.name_input, assets); Add(NameInput);
            Loading = new LoadingScreen(ui.loading, assets); Loading.Hide(); Add(Loading);
            Title = new TitleCard(ui.title, assets); Title.Hide(); Add(Title);
            Hud = new GameHud(ui.hud, assets); Hide(Hud); Add(Hud);
            HudChoicesOnly = string.Equals(ui.hud?.mode, "choices", System.StringComparison.OrdinalIgnoreCase);
            // Between-chapters screen: opt-in via manifest ui.chapter_end (absent
            // → chapters flow seamlessly, the historical behaviour).
            if (ui.chapter_end != null) { ChapterEnd = new ChapterEndScreen(ui.chapter_end, assets); Add(ChapterEnd); }
            Auth = (ui.auth != null && (ui.auth.enabled ?? true)) ? new AuthScreen(ui.auth, assets) : null;
            if (Auth != null) Add(Auth);
            Wardrobe = new WardrobeScreen(ui.wardrobe, assets); Wardrobe.SetManifest(_manifest);
            Wardrobe.Hide(); Add(Wardrobe);
            // Native skin: the sheet wears the game's dialogue panel + choice buttons.
            WardrobeStory = new WardrobeSheet(ui.wardrobe, ui.dialogue, ui.choices, assets);
            WardrobeStory.SetManifest(_manifest);
            WardrobeStory.OpenStore = () => OpenStoreAsync(); // the balance pills' "+"
            WardrobeStory.Hide(); _root.Add(WardrobeStory); // bottom sheet — keeps its own docked layout
            Store = new StoreScreen(ui.store, assets); Store.Hide(); Add(Store); // topmost overlay
            Settings = new SettingsScreen(ui.settings, assets);
            // "Sign in" closes settings and shows the boot auth screen (which sits
            // below settings in z-order, so we must hide settings first).
            if (Auth != null)
                Settings.OnSignIn = async () => { Settings.Hide(); await Auth.AskAsync(); };
            Settings.Hide(); Add(Settings);
            Detail = new TitleDetailScreen(assets); Detail.Hide(); Add(Detail);
            Gallery = new CgGalleryScreen(assets); Gallery.Hide(); Add(Gallery);
            Profile = new ProfileScreen(assets); Profile.Hide(); Add(Profile);
            Daily = new DailyRewardsScreen(assets); Daily.Hide(); Add(Daily);
            SkinShop = new SkinShopScreen(assets); SkinShop.Hide(); Add(SkinShop);
            PackShop = new PackShopScreen(assets); PackShop.Hide(); Add(PackShop);
            // The popup sits ABOVE everything so a "not enough currency → buy?"
            // confirm can appear over an open store/settings, and warnings over any.
            Popup = new PopupScreen(ui.popup); Popup.Hide(); Add(Popup);

            // Wallet → HUD pills: the server's balances mirror onto the in-game
            // strip whenever the wallet changes (earn/spend/IAP/refresh).
            _storeUi = ui.store;
            Lvn.Services.LvnWallet.Changed -= OnWalletChanged;
            Lvn.Services.LvnWallet.Changed += OnWalletChanged;
            OnWalletChanged();
        }

        private StoreConfig _storeUi;

        private void OnWalletChanged()
        {
            if (Hud == null) return;
            foreach (var kv in Lvn.Services.LvnWallet.Balances)
            {
                string icon = _storeUi?.currency_icons != null
                              && _storeUi.currency_icons.TryGetValue(kv.Key, out var u) ? u : null;
                Hud.SetBalance(kv.Key, kv.Value, icon);
            }
        }

        private void OnDestroy() => Lvn.Services.LvnWallet.Changed -= OnWalletChanged;

        /// <summary>Open the currency store overlay; completes when the player
        /// closes it. Safe from anywhere on the main thread (quick-menu item,
        /// HUD tap, a script's <c>ext store_show</c>).</summary>
        public Task OpenStoreAsync(CancellationToken ct = default)
            => Store != null ? Store.ShowAsync(ct) : Task.CompletedTask;

        /// <summary>Open the app-level settings overlay (sound, language, account,
        /// version, socials, legal). Completes when the player closes it.</summary>
        public Task OpenSettingsAsync(CancellationToken ct = default)
            => Settings != null ? Settings.ShowAsync(ct) : Task.CompletedTask;

        /// <summary>Open the rich detail page for a title; returns true if the player
        /// pressed Play/Continue. Configure Detail's fields before calling.</summary>
        public Task<bool> OpenDetailAsync(CancellationToken ct = default)
            => Detail != null ? Detail.ShowAsync(ct) : Task.FromResult(false);
        public Task OpenGalleryAsync(CancellationToken ct = default)
            => Gallery != null ? Gallery.ShowAsync(ct) : Task.CompletedTask;
        public Task OpenProfileAsync(CancellationToken ct = default)
            => Profile != null ? Profile.ShowAsync(ct) : Task.CompletedTask;
        public Task OpenDailyAsync(CancellationToken ct = default)
            => Daily != null ? Daily.ShowAsync(ct) : Task.CompletedTask;
        public Task OpenSkinShopAsync(CancellationToken ct = default)
            => SkinShop != null ? SkinShop.ShowAsync(ct) : Task.CompletedTask;
        public Task OpenPackShopAsync(CancellationToken ct = default)
            => PackShop != null ? PackShop.ShowAsync(ct) : Task.CompletedTask;

        /// <summary>Show a single-button notice over everything (a warning / info
        /// box). Completes when the player dismisses it. Safe from any main-thread
        /// caller (host code, a failed chapter-entry, a script op).</summary>
        public Task AlertAsync(string title, string message, string ok = null, CancellationToken ct = default)
            => Popup != null ? Popup.AlertAsync(title, message, ok, ct) : Task.CompletedTask;

        /// <summary>Show a two-button confirm; returns true if the player pressed
        /// the confirm button. Used e.g. for "not enough energy — buy?".</summary>
        public Task<bool> ConfirmAsync(string title, string message, string confirm = null,
                                       string cancel = null, CancellationToken ct = default)
            => Popup != null ? Popup.ConfirmAsync(title, message, confirm, cancel, ct) : Task.FromResult(false);

        /// <summary>Open the wardrobe overlay for a character (null → the
        /// configured/first one); completes when the player closes it.</summary>
        public Task OpenWardrobeAsync(string entityId = null, CancellationToken ct = default)
            => Wardrobe != null ? Wardrobe.ShowAsync(entityId, ct) : Task.CompletedTask;

        /// <summary>Open the in-story wardrobe sheet over the running scene —
        /// the actor on stage previews the browsing live. Completes on
        /// confirm/collapse; the wardrobe_show op awaits this.</summary>
        public Task OpenWardrobeSheetAsync(string entityId = null, CancellationToken ct = default)
            => WardrobeStory != null ? WardrobeStory.ShowAsync(entityId, ct) : Task.CompletedTask;

        /// <summary>Apply a live content update — swap in a freshly-fetched
        /// manifest and re-render the data-driven screens (the carousel rebuilds
        /// its deck, keeping the selected title). Cheap and safe to call any time;
        /// the host's content-sync loop calls it when the server version changes.</summary>
        public void ApplyLiveUpdate(LvnManifest manifest)
        {
            if (manifest == null) return;
            _manifest = manifest;
            Carousel?.SetTitles(manifest.titles);
            Hub?.SetData(manifest.collections, manifest.titles);
            Wardrobe?.SetManifest(manifest);
            WardrobeStory?.SetManifest(manifest);
        }

        /// <summary>Run the whole loop. <paramref name="bootReady"/> gates the boot
        /// splash; <paramref name="chapterReady"/> (optional) gates each chapter's
        /// loading bar; <paramref name="playChapter"/> plays the chosen chapter and
        /// returns when it finishes. Loops back to the carousel after each chapter.</summary>
        public async Task RunAsync(
            Func<bool> bootReady = null,
            Func<LvnChapter, Func<bool>> chapterReady = null,
            Func<LvnChapter, Func<float>> chapterProgress = null,
            Func<LvnTitle, LvnChapter, string, Task> playChapter = null,
            bool askName = true,
            CancellationToken ct = default,
            Func<float> bootProgress = null)
        {
            if (_root == null) throw new InvalidOperationException("Call Build() before RunAsync().");

            Boot.Hide();
            ShowOnly(); // hide all
            // ── boot splash ──
            Show(Boot);
            await Boot.RunAsync(bootReady ?? (() => true), bootProgress, ct);
            Hide(Boot);

            // ── auth screen (once, when the manifest enables it) ──
            // Its nickname doubles as the player name, so the name-input screen
            // is skipped when one was entered here.
            if (Auth != null)
            {
                try
                {
                    var nick = await Auth.AskAsync(ct);
                    if (!string.IsNullOrEmpty(nick)) _playerName = nick;
                }
                catch (OperationCanceledException) { return; }
            }

            // Hub browse (collections → cards → detail) vs the default carousel.
            bool useHub = _manifest.ui?.browse?.layout == "hub"
                          && _manifest.collections != null && _manifest.collections.Count > 0;

            while (!ct.IsCancellationRequested)
            {
                // ── choose a title: hub flow or the carousel ──
                LvnTitle title;
                if (useHub && Hub != null)
                {
                    Show(Hub);
                    title = await Hub.PickTitleAsync(ct);
                    if (ct.IsCancellationRequested) return;
                    Hide(Hub);
                    if (title == null) continue; // never picked → re-enter the hub
                }
                else
                {
                    Carousel.RefreshProgress(); // progress moved while a chapter played
                    Show(Carousel);
                    int idx = await WaitForPlay(ct);
                    if (ct.IsCancellationRequested) return;
                    Hide(Carousel);
                    title = (_manifest.titles != null && idx >= 0 && idx < _manifest.titles.Count)
                        ? _manifest.titles[idx] : null;
                }
                // "Играть" continues from the furthest STARTED chapter (started
                // ch2 → the button opens ch2); a fresh/finished title starts at
                // chapter one. PlayChapterAsync applies the same resume rule —
                // resolving it HERE too makes the loading screen show the right
                // chapter's backdrop and preload the right asset plan.
                var chapter = LvnProgress.Current(title) ?? FirstChapter(title);

                // ── name input (once) ──
                if (askName && string.IsNullOrEmpty(_playerName) && (_manifest.ui?.name_input != null))
                {
                    try { _playerName = await NameInput.AskAsync(ct); }
                    catch (OperationCanceledException) { return; }
                }

                // ── chapter loading (Liminal-style entry) ──
                // The loader stays OPAQUE while the chapter boots BEHIND it —
                // the host fades it out via RevealFromLoadingAsync() once the
                // scene has its first background, then floats the chapter title
                // over the LIVE scene (ShowChapterTitleAsync). No frame of raw
                // stage ever shows between screens.
                Show(Loading);
                var ready = chapterReady?.Invoke(chapter) ?? (() => true);
                var prog = chapterProgress?.Invoke(chapter);
                bool cached = ready();
                await Loading.RunAsync(ready, prog, ct, bgUrl: chapter?.bg_url,
                    minSecondsOverride: cached
                        ? (Transitions?.loading_floor ?? 0.25f)
                        : (float?)null);

                // ── play ──
                if (playChapter != null && chapter != null)
                {
                    _ = Lvn.Services.LvnWallet.RefreshAsync(); // fresh pills for the HUD
                    if (!HudChoicesOnly) Show(Hud); // "choices" mode: the stage event shows it
                    try { await playChapter(title, chapter, _playerName); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { Debug.LogWarning($"[shell] chapter play failed: {ex.Message}"); }
                    Hide(Hud);
                }
                // Safety: if play bailed before revealing (charge refused, script
                // fetch failed), don't strand an opaque loader over the menu.
                Loading.Hide();
                Title.Hide();
            }
        }

        /// <summary>Fade the (still opaque) chapter loader into whatever is on
        /// stage now. The host calls this once the scene is dressed — the swap
        /// reads as a single crossfade into the LIVE scene. Safe to call when
        /// the loader is already hidden (seamless chapter 2+).</summary>
        /// <summary>Manifest <c>ui.transitions</c> — between-screen pacing knobs
        /// (loader crossfade, cached floor, backdrop grace). Null = defaults.</summary>
        public TransitionsConfig Transitions { get; private set; }

        public async Task RevealFromLoadingAsync(CancellationToken ct = default)
        {
            bool visible = Loading != null && Loading.resolvedStyle.display != DisplayStyle.None;
            Debug.Log($"[shell] reveal: loader visible={visible} fade={Transitions?.screen_fade ?? 0.35f}s");
            if (!visible) return;
            await Loading.FadeOutAsync(Transitions?.screen_fade ?? 0.35f, ct);
            Loading.Hide();
        }

        /// <summary>Float the chapter-title card over the live scene (fade in,
        /// hold, fade out) — the Liminal entry: loader → live backdrop → name.</summary>
        public async Task ShowChapterTitleAsync(LvnChapter chapter, LvnTitle title, CancellationToken ct = default)
        {
            if (Title == null || chapter == null) return;
            Title.Set(ChapterLine(chapter), title?.name);
            Show(Title);
            await Title.RevealAsync(ct);
            Title.Hide();
        }

        /// <summary>Auto-start a title by id without racing the boot splash — the
        /// request is honoured the moment the carousel takes control. Returns false
        /// if no title carries that id. Pairs with <see cref="TitleCarousel.RequestPlay"/>.</summary>
        public bool RequestPlay(string titleId)
        {
            if (_manifest?.titles == null || Carousel == null) return false;
            for (int i = 0; i < _manifest.titles.Count; i++)
                if (_manifest.titles[i]?.id == titleId) { Carousel.RequestPlay(i); return true; }
            return false;
        }

        private Task<int> WaitForPlay(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(int i) { Carousel.OnPlay -= Handler; tcs.TrySetResult(i); }
            Carousel.OnPlay += Handler;
            // Honour a play requested before we got here (auto-start / deep-link fired
            // during the boot splash, when OnPlay had no subscriber yet).
            if (Carousel.TryConsumePendingPlay(out int pending))
            {
                Carousel.OnPlay -= Handler;
                tcs.TrySetResult(pending);
                return tcs.Task;
            }
            ct.Register(() => { Carousel.OnPlay -= Handler; tcs.TrySetCanceled(); });
            return tcs.Task;
        }

        /// <summary>The first playable chapter of a title (lowest non-negative
        /// chapter number across its seasons), or null.</summary>
        internal static LvnChapter FirstChapter(LvnTitle title)
        {
            if (title?.seasons == null) return null;
            LvnChapter best = null;
            foreach (var s in title.seasons)
            {
                if (s?.chapters == null) continue;
                foreach (var c in s.chapters)
                {
                    if (c == null) continue;
                    if (best == null || c.number < best.number) best = c;
                }
            }
            return best;
        }

        private static string ChapterLine(LvnChapter c) =>
            c == null ? "" : (c.number > 0 ? $"Chapter {c.number}" : "");

        private void Add(VisualElement el)
        {
            el.style.position = Position.Absolute;
            el.style.left = 0; el.style.right = 0; el.style.top = 0; el.style.bottom = 0;
            _root.Add(el);
        }

        private void ShowOnly()
        {
            Hide(Boot); Hide(Carousel); Hide(Hub); Hide(Loading); Hide(Title); Hide(Hud);
            NameInput.Hide();
            Auth?.Hide();
            Store?.Hide();
            Settings?.Hide();
            Detail?.Hide(); Gallery?.Hide(); Profile?.Hide(); Daily?.Hide();
            SkinShop?.Hide(); PackShop?.Hide();
            Wardrobe?.Hide();
            WardrobeStory?.Hide();
        }

        private static void Show(VisualElement el) { if (el != null) el.style.display = DisplayStyle.Flex; }
        private static void Hide(VisualElement el) { if (el != null) el.style.display = DisplayStyle.None; }
    }
}
