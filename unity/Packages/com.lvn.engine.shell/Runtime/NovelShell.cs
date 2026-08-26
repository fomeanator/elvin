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

        private UIDocument _doc;
        private VisualElement _root;
        private LvnManifest _manifest;
        private ILvnAssets _assets;
        /// <summary>Единый индикатор загрузок; хост навешивает на него центр
        /// очереди и данные офлайна после Build.</summary>
        public Lvn.UI.Screens.DownloadHud DownloadHud;
        /// <summary>Единый навбар приложения (лого, кружок, валюты, бургер).</summary>
        public Lvn.UI.Screens.LvnTopBar TopBar;

        private void OnWalletPills() => TopBar?.RefreshBalances();

        // ── НАВИГАТОР ЛЕНТЫ (решение Ильи 26.08: «один уезжает — другой
        // приезжает») ── ОДНО состояние _tab; страница «уехала» = display:none
        // (translate — только анимация, никогда не состояние). Гонки отрезаны
        // флагом занятости.
        private int _tab;
        private bool _tabBusy;
        private float _tabCanvasX; // смещение полотна: четверть на вкладку
        private VisualElement _canvasTint; // «шейдер-лайт»: тон вкладки поверх фото

        // Настроение каждой вкладки на полотне (пока тинтом; настоящие fx —
        // после переноса полотна на канвас, где живут наши шейдеры).
        // «Реализм на все» (Илья 26.08): цветные настроения вкладок сняты —
        // фото чистое на всех экранах. Слоты оставлены под будущие пресеты.
        private static readonly Color[] TabTints =
        {
            Color.clear, Color.clear, Color.clear, Color.clear,
        };

        private (VisualElement el, LvnOverlayScreen scr) TabPage(int i) => i switch
        {
            0 => (Hub?.ContentRoot, null),
            1 => (PackShop, PackShop),
            2 => (WardrobeTab, WardrobeTab),
            3 => (Profile, Profile),
            _ => (null, null),
        };

        public async Task TabGoTo(int target)
        {
            if (_tabBusy || target == _tab) return;
            var to = TabPage(target);
            if (to.el == null) return;
            _tabBusy = true;
            try
            {
                var from = TabPage(_tab);
                int dir = target > _tab ? 1 : -1;
                float w = _root.resolvedStyle.width;
                if (w <= 0f || float.IsNaN(w)) w = 1080f;

                to.scr?.ShowAsTab();
                to.el.style.display = DisplayStyle.Flex;
                to.el.style.translate = new Translate(dir * w, 0f);
                Hub?.SetActiveTab(target);

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var fromEl = from.el;
                float canvasFrom = _tabCanvasX, canvasTo = target * w * 0.2f;
                to.el.experimental.animation.Start(0f, 1f, 260, (e, p) =>
                {
                    float k = 1f - Mathf.Pow(1f - p, 3f);
                    e.style.translate = new Translate(Mathf.Lerp(dir * w, 0f, k), 0f);
                    if (fromEl != null)
                        fromEl.style.translate = new Translate(Mathf.Lerp(0f, -dir * w, k), 0f);
                    _tabCanvasX = Mathf.Lerp(canvasFrom, canvasTo, k); // полотно едет с нами
                    if (_canvasTint != null)
                        _canvasTint.style.backgroundColor = Color.Lerp(
                            TabTints[Mathf.Clamp(_tab, 0, 3)], TabTints[Mathf.Clamp(target, 0, 3)], k);
                    if (p >= 1f) tcs.TrySetResult(true);
                });
                await tcs.Task;
                _tabCanvasX = canvasTo;

                if (from.scr != null) from.scr.HideAsTab();
                else if (fromEl != null) fromEl.style.display = DisplayStyle.None;
                if (fromEl != null) fromEl.style.translate = new Translate(0f, 0f);
                to.el.style.translate = new Translate(0f, 0f);
                _tab = target;
            }
            finally { _tabBusy = false; }
        }

        /// <summary>Мгновенно домой (гардероб/старт главы): без анимации.</summary>
        public void TabReset()
        {
            var from = TabPage(_tab);
            if (from.scr != null) from.scr.HideAsTab();
            var home = TabPage(0);
            if (home.el != null)
            {
                home.el.style.display = DisplayStyle.Flex;
                home.el.style.translate = new Translate(0f, 0f);
            }
            _tab = 0;
            _tabCanvasX = 0f;
            Hub?.SetActiveTab(0, instant: true);
        }

        private VisualElement _atmosphere;
        /// <summary>Кукла героини поверх полотна меню (все вкладки).</summary>
        public Lvn.UI.Screens.MenuHeroine MenuHeroineView;
        /// <summary>Вкладка гардероба — UI вокруг общей героини.</summary>
        public WardrobeTabScreen WardrobeTab;

        private void BuildAtmosphere()
        {
            _atmosphere?.RemoveFromHierarchy();
            var t = LvnTheme.Current;
            // ПОЛОТНО В 4 ЭКРАНА (концепция Ильи и партнёра): один большой фон
            // по горизонтали; каждая вкладка меню смотрит в свою четверть,
            // переезд вкладок плавно везёт полотно (TabGoTo). Пока полотно —
            // атмосфера темы; арт-полотно партнёра ляжет сюда же данными.
            _atmosphere = new VisualElement { pickingMode = PickingMode.Ignore };
            _atmosphere.style.position = Position.Absolute;
            _atmosphere.style.left = 0; _atmosphere.style.top = 0; _atmosphere.style.bottom = 0;
            // ПАРАЛЛАКС-ГЛУБИНА (уточнение Ильи): фон ОДИН, шириной 160%
            // экрана — за вкладку он сдвигается на долю (излишек ширины /3),
            // отставая от страниц: страницы едут на экран, фон — на пятую.
            _atmosphere.style.width = Length.Percent(160f);
            _atmosphere.style.backgroundColor = t.Bg;
            var canvasUrl = _manifest?.ui?.browse?.canvas;
            if (!string.IsNullOrEmpty(canvasUrl))
            {
                // Арт-полотно партнёра: фото на всю ширину 4 экранов + тёмная
                // вуаль (текст обязан читаться) + тинт вкладки поверх.
                var photo = new VisualElement { pickingMode = PickingMode.Ignore };
                photo.style.position = Position.Absolute;
                photo.style.left = 0; photo.style.right = 0;
                photo.style.top = 0; photo.style.bottom = 0;
                photo.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                _atmosphere.Add(photo);
                LvnAsync.Fire(ScreenUi.AssignBgAsync(photo, canvasUrl, _assets), "MenuCanvas");
                var veil = new VisualElement { pickingMode = PickingMode.Ignore };
                veil.style.position = Position.Absolute;
                veil.style.left = 0; veil.style.right = 0;
                veil.style.top = 0; veil.style.bottom = 0;
                // «Реализм» (Илья): фото почти как есть — лишь лёгкая вуаль,
                // чтобы текст поверх оставался читабельным.
                veil.style.backgroundColor = new Color(t.Bg.r, t.Bg.g, t.Bg.b, 0.22f);
                _atmosphere.Add(veil);
                _canvasTint = new VisualElement { pickingMode = PickingMode.Ignore };
                _canvasTint.style.position = Position.Absolute;
                _canvasTint.style.left = 0; _canvasTint.style.right = 0;
                _canvasTint.style.top = 0; _canvasTint.style.bottom = 0;
                _atmosphere.Add(_canvasTint);
            }
            else LvnBackdrop.Apply(_atmosphere, t);
            _root.Insert(0, _atmosphere);

            // Героиня — НЕПОДВИЖНЫЙ передний план меню: полотно и контент едут,
            // она стоит (слой между полотном и вкладками).
            MenuHeroineView = new Lvn.UI.Screens.MenuHeroine(_manifest, _assets);
            _root.Insert(1, MenuHeroineView);
            // ВИДИМОСТЬ ПО ПРАВИЛУ «виден экран меню», а не «нет главы»:
            // гардероб из хаба прячет хаб и живёт в документе СЦЕНЫ — атмосфера
            // с событийной подпиской оставалась поверх и заслоняла его целиком
            // (живой скрин «гардероб сломан»). Тик ниже держит правило сам.
            _root.schedule.Execute(() =>
            {
                bool menuVisible =
                    (Boot != null && Boot.style.display == DisplayStyle.Flex) ||
                    (Carousel != null && Carousel.style.display == DisplayStyle.Flex) ||
                    (Hub != null && Hub.style.display == DisplayStyle.Flex);
                var want = menuVisible ? DisplayStyle.Flex : DisplayStyle.None;
                if (_atmosphere.style.display != want) _atmosphere.style.display = want;
                // Героиня — часть меню-полотна: живёт и гаснет вместе с ним
                // (иначе кукла торчала бы поверх сцены в игре).
                if (MenuHeroineView != null && MenuHeroineView.HasEntity)
                    MenuHeroineView.style.display = want;
            }).Every(100);

            // Параллакс: постоянный медленный дрейф (фон ЖИВЁТ сам), плюс
            // скролл ленты хаба и наклон телефона; слои — на разной глубине.
            var layers = new System.Collections.Generic.List<VisualElement>();
            _atmosphere.Query<VisualElement>("lvn-backdrop").ForEach(layers.Add);
            Vector2 tilt = Vector2.zero;
            _root.schedule.Execute(() =>
            {
                if (_atmosphere.style.display == DisplayStyle.None) return;
                float time = Time.realtimeSinceStartup;
                float scroll = Hub != null && Hub.style.display == DisplayStyle.Flex ? Hub.ScrollY : 0f;
                var acc = UnityEngine.Input.acceleration;
                var target = new Vector2(
                    Mathf.Clamp(acc.x, -0.5f, 0.5f),
                    Mathf.Clamp(acc.y + 0.8f, -0.5f, 0.5f));
                tilt = Vector2.Lerp(tilt, target, 0.06f);
                // ПОЛОТНО ЕДЕТ ВСЕГДА (грабля: с фото-артом слоёв нет, и ранний
                // выход оставлял его неподвижным при переездах вкладок).
                _atmosphere.style.translate = new Translate(-_tabCanvasX, 0f);
                for (int i = 0; i < layers.Count; i++)
                {
                    // Сумма сдвигов ОБЯЗАНА жить в напуске слоя (80px), иначе
                    // у кромки экрана оголяется шов: глубина ограничена тремя
                    // ступенями, вклад скролла закэмплен.
                    float k = Mathf.Min(i + 1, 3);
                    float driftX = Mathf.Sin(time * 0.11f + i * 1.7f) * 6f * k;
                    float driftY = Mathf.Cos(time * 0.09f + i * 2.3f) * 5f * k;
                    float scrollY = Mathf.Clamp(scroll * (0.05f + 0.045f * i), 0f, 30f);
                    layers[i].style.translate = new Translate(
                        driftX + tilt.x * 8f * k,
                        driftY - scrollY + tilt.y * 6f * k);
                }
            }).Every(33);
        }
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
            // Отклик на нажатие — на КОРЕНЬ, то есть сразу на все экраны
            // оболочки. Ставить его поэкранно значит однажды забыть.
            LvnMotion.EnableTapFeedback(_root);
            // По той же причине здесь и шрифт темы: unityFontDefinition
            // наследуется вниз, и вся оболочка получает одну гарнитуру.
            LvnFonts.ApplyDefault(_root);

            Boot = new BootScreen(ui.boot, assets); Boot.Hide(); Add(Boot);
            // Единая атмосфера меню (решение Ильи 26.08): ОДИН живой
            // параллакс-фон под всеми экранами оболочки. Создаётся после
            // хаба (он выбирает тему), встаёт ПЕРВЫМ ребёнком корня; в игре
            // прячется — сцена живёт в документе ПОД оболочкой.
            Carousel = new TitleCarousel(_manifest.titles, ui.carousel, assets); Hide(Carousel); Add(Carousel);
            Hub = new BrowseHub(ui.browse, assets); Hub.SetData(_manifest.collections, _manifest.titles);
            Hide(Hub); Add(Hub);
            BuildAtmosphere();
            Loading = new LoadingScreen(ui.loading, assets); Loading.Hide(); Add(Loading);
            Title = new TitleCard(ui.title, assets); Title.Hide(); Add(Title);
            Hud = new GameHud(ui.hud, assets); Hide(Hud); Add(Hud);
            HudChoicesOnly = string.Equals(ui.hud?.mode, "choices", System.StringComparison.OrdinalIgnoreCase);
            // Between-chapters screen: opt-in via manifest ui.chapter_end (absent
            // → chapters flow seamlessly, the historical behaviour).
            if (ui.chapter_end != null) { ChapterEnd = new ChapterEndScreen(ui.chapter_end, assets); Add(ChapterEnd); }
            Auth = (ui.auth != null && (ui.auth.enabled ?? true)) ? new AuthScreen(ui.auth, assets) : null;
            if (Auth != null) Add(Auth);
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

            // ── СЛОИ (решение Ильи 26.08: «расставь нормально слои») ──
            // Порядок add'ов истории — не архитектура: настройки оказывались
            // ПОД магазином. Теперь явные слои: ВКЛАДКИ (магазин/профиль), над
            // ними ХАБ (его нижнее меню живёт поверх разделов и переключает
            // их), затем ПОПАПЫ, затем алерты; навбар и кружок — выше всех
            // (добавляются после).
            var tabsLayer = new VisualElement { name = "lvn-layer-tabs", pickingMode = PickingMode.Ignore };
            ScreenUi.Stretch(tabsLayer);
            var popupLayer = new VisualElement { name = "lvn-layer-popups", pickingMode = PickingMode.Ignore };
            ScreenUi.Stretch(popupLayer);
            void Reparent(VisualElement el, VisualElement layer)
            { if (el != null) { el.RemoveFromHierarchy(); layer.Add(el); } }
            WardrobeTab = new WardrobeTabScreen(_manifest, _assets);
            WardrobeTab.Hide();
            Reparent(PackShop, tabsLayer);
            Reparent(WardrobeTab, tabsLayer);
            Reparent(Profile, tabsLayer);
            Reparent(Hub, tabsLayer); // хаб ПОСЛЕДНИМ — его нав поверх вкладок
            Reparent(Settings, popupLayer);
            Reparent(Detail, popupLayer);
            Reparent(Gallery, popupLayer);
            Reparent(Daily, popupLayer);
            Reparent(SkinShop, popupLayer);
            _root.Add(tabsLayer);
            _root.Add(popupLayer);
            Popup.RemoveFromHierarchy();
            _root.Add(Popup); // алерты — над попапами

            // Единая пилюля загрузки — поверх ВСЕГО (даже попапа): «Скачать
            // всё», прелоад главы и стриминг видны из любого экрана, а не
            // только пока открыты настройки (живой репорт «закрыл — и
            // остановилось»: батч жил, но был невидим).
            // ЕДИНЫЙ НАВБАР — один верх на всё приложение (меню и игра);
            // кружок загрузок — отдельный оверлей ПОВЕРХ бара, в центре его
            // строки (морф попапа растёт из центра).
            TopBar = new Lvn.UI.Screens.LvnTopBar();
            Add(TopBar);
            OnChapterSessionStart += () => { TopBar.SetInGame(true); DownloadHud?.SetInGame(true); };
            OnChapterSessionEnd += () => { TopBar.SetInGame(false); DownloadHud?.SetInGame(false); };
            Lvn.Services.LvnWallet.Changed -= OnWalletPills;
            Lvn.Services.LvnWallet.Changed += OnWalletPills;

            if (assets is CachingAssets ca)
            {
                DownloadHud = new Lvn.UI.Screens.DownloadHud();
                Add(DownloadHud);
                _root.schedule.Execute(() =>
                {
                    DownloadHud.Tick(ca.Loader.Transfers());
                    // Safe area: бар и кружок сидят ПОД вырезом камеры.
                    float safe = Screen.height > 0
                        ? Screen.safeArea.y / (float)Screen.height * _root.resolvedStyle.height
                        : 0f;
                    if (!float.IsNaN(safe))
                    {
                        TopBar.SetSafeTop(safe);
                        DownloadHud.SetSafeTop(safe);
                    }
                    TopBar.SyncTapZone(); // зона и декор уступают модали сцены
                    DownloadHud.SetSceneModal(
                        !(TopBar.TapZoneAvailable?.Invoke() ?? true));
                }).Every(300);
            }

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

        /// <summary>ONE store: every entry (quick menu, wallet "+", scripts'
        /// <c>ext store_show</c>, the hub) opens the pack shop.</summary>
        public Task OpenStoreAsync(CancellationToken ct = default)
            => OpenPackShopAsync(ct);

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
            Func<float> bootProgress = null,
            bool bootSplash = true)
        {
            if (_root == null) throw new InvalidOperationException("Call Build() before RunAsync().");

            Boot.Hide();
            ShowOnly(); // hide all
            // ── boot splash ──
            // bootSplash=false: the host's own boot surface (NovelApp's engine
            // veil) already covers this wait — showing a SECOND loading screen
            // under it would flash a second bar at the hand-off. Wait silently.
            if (bootSplash)
            {
                Show(Boot);
                await Boot.RunAsync(bootReady ?? (() => true), bootProgress, ct);
                Hide(Boot);
            }
            else
            {
                var ready = bootReady ?? (() => true);
                while (!ready() && !ct.IsCancellationRequested)
                    await Task.Yield();
                if (ct.IsCancellationRequested) return;
            }

            // The player's name persists across launches — nobody re-asks it.
            _playerName = Lvn.UI.LvnPrefs.PlayerName;

            // ── welcome/auth screen: the FIRST launch only ──
            // Later launches go straight in; the device sign-in runs silently
            // either way. A nickname entered here seeds the player name.
            // ВВОДНАЯ ГЛАВА ИДЁТ ПЕРВОЙ И БЕЗ ВОПРОСОВ. Пока она не пройдена, у
            // игрока не спрашивают ни имени, ни новеллы: он попадает прямо в
            // историю, а она сама и знакомится, и объясняет правила. Витрина
            // ждёт своей очереди — см. IntroTitle ниже.
            var introTitle = PendingIntroTitle();
            if (Auth != null && !Lvn.UI.LvnPrefs.SeenWelcome && introTitle == null)
            {
                try
                {
                    var nick = await Auth.AskAsync(ct);
                    Lvn.UI.LvnPrefs.SeenWelcome = true;
                    if (!string.IsNullOrEmpty(nick))
                    {
                        _playerName = nick;
                        Lvn.UI.LvnPrefs.PlayerName = nick;
                    }
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
                var intro = PendingIntroTitle();
                if (intro != null)
                {
                    title = intro;   // выбора нет — и это намеренно
                }
                else if (useHub && Hub != null)
                {
                    Show(Hub);
                    Hub.PlayEntrance();      // контент фейдом, нижняя навигация снизу
                    TopBar?.PlayEntrance();  // верхний бар сверху — один ансамбль
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

                // The name ask lives INSIDE the chapter entry now (after the
                // title card, over the live scene) — the host owns it.

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
                    LvnAsync.Fire(Lvn.Services.LvnWallet.RefreshAsync(), "Refresh"); // fresh pills for the HUD
                    // Полоса GameHud удалена (решение Ильи 26.08): затемнение
                    // сверху убрано, прогресс и валюта живут МИНИ-БАБЛИКАМИ
                    // единого навбара по углам сцены.
                    OnChapterSessionStart?.Invoke(); // меню-музыка и прочее «вне новеллы» глохнет
                    try { await playChapter(title, chapter, _playerName); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { Debug.LogWarning($"[shell] chapter play failed: {ex.Message}"); }
                    OnChapterSessionEnd?.Invoke();   // вернулись в меню — и его звук тоже
                    Hide(Hud);
                }
                // Вводная считается пройденной, когда доиграна до конца: бросил
                // на середине — при следующем запуске снова попадёт в неё, а не
                // на витрину, которую ещё не заслужил.
                if (intro != null && IsTitleFinished(intro)) Lvn.UI.LvnPrefs.IntroDone = true;

                // Safety: if play bailed before revealing (charge refused, script
                // fetch failed), don't strand an opaque loader over the menu.
                Loading.Hide();
                Title.Hide();
                if (BootVeil.IsVisible) BootVeil.Hide(); // и брендовую вуаль первого входа
            }
        }

        /// <summary>Вводная новелла, которую ещё не прошли, или null. Новелла
        /// объявляет себя вводной полем <c>type: "intro"</c> в манифесте — как и
        /// всякий другой вид новеллы, данными, а не кодом оболочки.</summary>
        /// <summary>Сессия главы началась/кончилась — для всего, что живёт
        /// ТОЛЬКО вне новеллы (музыка меню и т.п.): хост глушит на старте и
        /// возвращает по выходу в меню.</summary>
        public Action OnChapterSessionStart;
        public Action OnChapterSessionEnd;

        /// <summary>Первый вход ещё впереди (вводная не пройдена): хост держит
        /// брендовую вуаль вместо полос — см. NovelApp.DriveBootVeilAsync.</summary>
        public bool HasPendingIntro => PendingIntroTitle() != null;

        private LvnTitle PendingIntroTitle()
        {
            if (Lvn.UI.LvnPrefs.IntroDone)
            {
                Debug.Log("[lvn-intro] ворота: IntroDone=true (метка устройства) — витрина");
                return null;
            }
            if (_manifest?.titles == null) return null;
            foreach (var t in _manifest.titles)
                if (t != null && string.Equals(t.type, "intro", StringComparison.OrdinalIgnoreCase))
                {
                    bool done = IsTitleFinished(t);
                    // Диагностический след: «почему не стартанула воронка» иначе
                    // выясняется раскопками PlayerPrefs на чужом устройстве.
                    Debug.Log($"[lvn-intro] ворота: '{t.id}' reached={LvnProgress.Reached(t)} "
                        + $"current={(LvnProgress.Current(t)?.id ?? "-")} → "
                        + (done ? "пройдена, витрина" : "играем воронку"));
                    return done ? null : t;
                }
            Debug.Log("[lvn-intro] ворота: intro-тайтла в манифесте нет — витрина");
            return null;
        }

        /// <summary>Новелла пройдена, если дошли до её последней главы и она
        /// закончилась: продолжения нет, а самая дальняя достигнутая — последняя.</summary>
        private static bool IsTitleFinished(LvnTitle t)
        {
            var chapters = t.ChaptersOf();
            if (chapters.Count == 0) return false;
            int reached = LvnProgress.Reached(t);
            // НЕ НАЧАТА — ЗНАЧИТ НЕ ПРОЙДЕНА. Без этой строки новелла, чья
            // первая глава имеет номер 0, считалась пройденной на чистом
            // устройстве: «дошёл до 0» ≥ «последняя 0». Воронка не включалась
            // ни разу, и игрок сразу видел витрину.
            if (reached <= 0) return false;
            return LvnProgress.Current(t) == null
                && reached >= chapters[chapters.Count - 1].number;
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
            if (visible)
            {
                await Loading.FadeOutAsync(Transitions?.screen_fade ?? 0.35f, ct);
                Loading.Hide();
            }
            // Брендовая вуаль первого входа (имя продукта фейдом) живёт до
            // одетой сцены и гаснет ЗДЕСЬ — единственный кроссфейд, который
            // видит игрок: имя → первая сцена. Ни полос, ни экрана загрузки.
            if (BootVeil.IsVisible) await BootVeil.FadeOutAsync(0.6f);
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
            Auth?.Hide();
            Settings?.Hide();
            Detail?.Hide(); Gallery?.Hide(); Profile?.Hide(); Daily?.Hide();
            SkinShop?.Hide(); PackShop?.Hide();
        }

        private static void Show(VisualElement el) { if (el != null) el.style.display = DisplayStyle.Flex; }
        private static void Hide(VisualElement el) { if (el != null) el.style.display = DisplayStyle.None; }
    }
}
