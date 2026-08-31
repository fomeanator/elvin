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
    public sealed partial class NovelShell : MonoBehaviour
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

        /// <summary>Как новелла обставляет вход в главу (ui.portal). Есть блок
        /// — створ стоит НА ГЛАВНОЙ, и героиня уходит в него; нет — обычный
        /// экран загрузки. Отдельного экрана у перехода нет: он был лишней
        /// остановкой между решением игрока и историей.</summary>
        public Lvn.Content.PortalConfig Portal { get; private set; }
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
        /// <summary>Таблица лидеров. Экран был написан и переведён (подписи
        /// лежат в ui.words), но НЕ СОЗДАВАЛСЯ ни в одном месте: игрок не мог
        /// его увидеть никаким путём, а сервис умел отдавать данные.</summary>
        public LeaderboardScreen Leaderboard { get; private set; }
        /// <summary>The wardrobe / skin shop.</summary>
        public SkinShopScreen SkinShop { get; private set; }
        /// <summary>The currency-pack shop — ВКЛАДКА ленты (прозрачная страница).</summary>
        public PackShopScreen PackShop { get; private set; }
        /// <summary>Быстрый магазин — МОДАЛЬ со своим фоном: плюсик валют, гейт
        /// энергии, ext store_show; открывается поверх любой страницы и в игре.</summary>
        public PackShopScreen PackShopModal { get; private set; }
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

        private readonly LvnLeash _leash = new LvnLeash();

        /// <summary>Отпустить всё, на что подписана оболочка. Зовёт хост при
        /// сносе: у оболочки своего OnDestroy нет — она не MonoBehaviour.</summary>
        public void ReleaseSubscriptions() => _leash.Release();

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





        // «Идёт ли глава» знал каждый файл своей копией; теперь это ОДНА
        // правда Режиссёра, а здесь — окно в неё («назад» в игре принадлежит
        // сцене, не ленте).
        private bool _inChapter => Lvn.UI.LvnScreenDirector.Current.InChapter;


        /// <summary>Переезд вкладки (from, to) — хост панорамирует сцену меню.</summary>
        public Action<int, int> OnTabTravel;
        /// <summary>Тик переезда вкладок: eased-прогресс 0..1 КАЖДЫЙ кадр
        /// анимации — хост ведёт полотно сцены той же кривой, кадр в кадр с UI
        /// (свой таймер фона запаздывал — рассинхрон, живой репорт 28.08).</summary>
        public Action<float> OnTabTravelTick;



        private VisualElement _atmosphere;
        private bool _sceneMenu; // меню рисуется сценой: оболочка прозрачна
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
            _atmosphere.style.width = Length.Percent(125f); // запас под сдвиг 3×0.067W
            _atmosphere.style.backgroundColor = t.Bg;
            var canvasUrl = _manifest?.ui?.browse?.canvas;
            _sceneMenu = !string.IsNullOrEmpty(canvasUrl);
            bool sceneMenu = _sceneMenu;
            if (sceneMenu)
            {
                // МЕНЮ ВНУТРИ ИГРЫ: полотно и героиню рисует СЦЕНА (канвас под
                // панелью) — оболочка прозрачна, атмосфера мертва совсем.
                _atmosphere.style.display = DisplayStyle.None;
                _atmosphere.style.backgroundColor = Color.clear;
            }
            else if (!sceneMenu && canvasUrl != null)
            {
                // Арт-полотно партнёра: фото на всю ширину 4 экранов + тёмная
                // вуаль (текст обязан читаться) + тинт вкладки поверх.
                var photo = new VisualElement { pickingMode = PickingMode.Ignore };
                LvnChrome.Stretch(photo);
                _atmosphere.Add(photo);
                LvnPicture.Photo(photo, canvasUrl, _assets, what: "MenuCanvas");
                var veil = new VisualElement { pickingMode = PickingMode.Ignore };
                LvnChrome.Stretch(veil);
                // «Реализм» (Илья): фото почти как есть — лишь лёгкая вуаль,
                // чтобы текст поверх оставался читабельным.
                veil.style.backgroundColor = new Color(t.Bg.r, t.Bg.g, t.Bg.b, 0.22f);
                _atmosphere.Add(veil);
                _canvasTint = new VisualElement { pickingMode = PickingMode.Ignore };
                LvnChrome.Stretch(_canvasTint);
                _atmosphere.Add(_canvasTint);
            }
            else LvnBackdrop.Apply(_atmosphere, t);
            _root.Insert(0, _atmosphere);

            // ГЕРОИНЮ РИСУЕТ СЦЕНА — ВСЕГДА И ТОЛЬКО ОНА. Здесь стояла вторая
            // кукла: те же слои, собранные оболочкой в UI-элементе. Двух
            // реализаций одного человека хватало, чтобы каждый вопрос «почему
            // она такая» начинался с «а кто её сейчас рисует», и чтобы всякая
            // правка делалась дважды. Фигура одна, дом у неё один (VnStage).
            // ВИДИМОСТЬ ПО ПРАВИЛУ «виден экран меню», а не «нет главы»:
            // гардероб из хаба прячет хаб и живёт в документе СЦЕНЫ — атмосфера
            // с событийной подпиской оставалась поверх и заслоняла его целиком
            // (живой скрин «гардероб сломан»). Тик ниже держит правило сам.
            _root.schedule.Execute(() =>
            {
                if (_sceneMenu) return; // сцена рисует меню — атмосфера мертва
                bool menuVisible =
                    (Boot != null && Boot.style.display == DisplayStyle.Flex) ||
                    (Carousel != null && Carousel.style.display == DisplayStyle.Flex) ||
                    (Hub != null && Hub.style.display == DisplayStyle.Flex);
                var want = menuVisible ? DisplayStyle.Flex : DisplayStyle.None;
                if (_atmosphere.style.display != want) _atmosphere.style.display = want;
            }).Every(100);

            // Параллакс: постоянный медленный дрейф (фон ЖИВЁТ сам), плюс
            // скролл ленты хаба и наклон телефона; слои — на разной глубине.
            var layers = new System.Collections.Generic.List<VisualElement>();
            _atmosphere.Query<VisualElement>("lvn-backdrop").ForEach(layers.Add);
            Vector2 tilt = Vector2.zero;
            _root.schedule.Execute(() =>
            {
                if (_atmosphere.style.display == DisplayStyle.None) return;
                float time = Lvn.LvnClock.Now();
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
        // Тоже окно в дом, а не копия: оболочка спрашивает имя, а не хранит.
        private string _playerName => Lvn.UI.LvnPlayerName.Current;

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
            // СЛОВАРЬ СМЕНИЛСЯ — ЭКРАНЫ ПЕРЕОДЕВАЮТСЯ. Подписи ставятся при
            // сборке экрана, и без этого перевод доезжал бы только до того, что
            // откроют ПОСЛЕ смены языка: открытые настройки остались бы на
            // прежнем языке, и игрок решил бы, что переключатель не работает.
            // Корень оболочки — один из живых деревьев игры; переодеванием
            // заведует дом, и подписываться каждому экрану не нужно.
            // На смену слов и шрифта подписан САМ ДОМ: объявить корень —
            // единственное, что тут нужно. Подписка жила здесь, и сцена без
            // оболочки (песочница, демо) не переодевалась вовсе.
            Lvn.UI.LvnRedress.Register(_root);

            Boot = new BootScreen(ui.boot, assets); Add(Boot);
            // Единая атмосфера меню (решение Ильи 26.08): ОДИН живой
            // параллакс-фон под всеми экранами оболочки. Создаётся после
            // хаба (он выбирает тему), встаёт ПЕРВЫМ ребёнком корня; в игре
            // прячется — сцена живёт в документе ПОД оболочкой.
            Carousel = new TitleCarousel(_manifest.titles, ui.carousel, assets); Add(Carousel);
            Hub = new BrowseHub(ui.browse, assets); Hub.SetData(_manifest.collections, _manifest.titles);
            Add(Hub);
            BuildAtmosphere();
            Loading = new LoadingScreen(ui.loading, assets); Add(Loading);
            Title = new TitleCard(ui.title, assets); Add(Title);
            Hud = new GameHud(ui.hud, assets); Add(Hud);
            HudChoicesOnly = string.Equals(ui.hud?.mode, "choices", System.StringComparison.OrdinalIgnoreCase);
            // Between-chapters screen: opt-in via manifest ui.chapter_end (absent
            // → chapters flow seamlessly, the historical behaviour).
            if (ui.chapter_end != null) { ChapterEnd = new ChapterEndScreen(ui.chapter_end, assets); Add(ChapterEnd); }
            if (ui.portal != null && (ui.portal.enabled ?? true)) Portal = ui.portal;
            Auth = (ui.auth != null && (ui.auth.enabled ?? true)) ? new AuthScreen(ui.auth, assets) : null;
            if (Auth != null) Add(Auth);
            // Образцу текста нужен кегль реплик ЭТОЙ новеллы: настраивают
            // размер здесь, а видят его в главе — образец обязан совпасть.
            if (ui.settings != null && ui.settings.sample_font_size == null && ui.dialogue?.body_size != null)
                ui.settings.sample_font_size = ui.dialogue.body_size;
            Settings = new SettingsScreen(ui.settings, assets);
            // "Sign in" closes settings and shows the boot auth screen (which sits
            // below settings in z-order, so we must hide settings first).
            if (Auth != null)
                Settings.OnSignIn = async () => { Settings.Hide(); await Auth.AskAsync(); };
            Add(Settings);
            Detail = new TitleDetailScreen(assets); Add(Detail);
            Gallery = new CgGalleryScreen(assets); Add(Gallery);
            Profile = new ProfileScreen(assets); Add(Profile);
            Daily = new DailyRewardsScreen(assets); Add(Daily);
            Leaderboard = new LeaderboardScreen(assets); Add(Leaderboard);
            SkinShop = new SkinShopScreen(assets); Add(SkinShop);
            PackShop = new PackShopScreen(assets); Add(PackShop);
            PackShopModal = new PackShopScreen(assets, modal: true); Add(PackShopModal);
            // Какую площадку показывать «бесплатной» карточкой — решает игра
            // (ui.store.ad_placement): движок не выбирает за неё, чем торговать.
            PackShop.AdPlacement = ui.store?.ad_placement;
            PackShopModal.AdPlacement = ui.store?.ad_placement;
            // The popup sits ABOVE everything so a "not enough currency → buy?"
            // confirm can appear over an open store/settings, and warnings over any.
            Popup = new PopupScreen(ui.popup); Add(Popup);

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
            _tabsLayer = tabsLayer; _popupLayer = popupLayer;
            void Reparent(VisualElement el, VisualElement layer)
            { if (el != null) { el.RemoveFromHierarchy(); layer.Add(el); } }
            WardrobeTab = new WardrobeTabScreen(_manifest, _assets);
            Add(WardrobeTab);
            // Покупка в меню-гардеробе идёт через кошелёк; нехватка средств
            // ведёт в БЫСТРЫЙ модальный магазин прямо поверх вкладки.
            WardrobeTab.OpenStore = () => OpenPackShopAsync();
            WardrobeTab.ConfirmTopUp = (t, m) => ConfirmAsync(t, m, LvnWords.Of("store.go", "Store"), LvnWords.Of("common.cancel", "Cancel"));
            WardrobeTab.Alert = (t, m) => AlertAsync(t, m);
            Reparent(PackShop, tabsLayer);
            Reparent(WardrobeTab, tabsLayer);
            Reparent(Profile, tabsLayer);
            Reparent(Hub, tabsLayer); // хаб ПОСЛЕДНИМ — его нав поверх вкладок
            Reparent(Settings, popupLayer);
            Reparent(Detail, popupLayer);
            Reparent(Gallery, popupLayer);
            Reparent(Daily, popupLayer);
            Reparent(SkinShop, popupLayer);
            Reparent(PackShopModal, popupLayer);
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
            AddChrome(TopBar);
            // Приложение поднимается — экран чист. Режиссёр статический, и в
            // редакторе Stop→Play его память переживает прогон: без сброса
            // «глава идёт» досталась бы в наследство от прошлого запуска.
            Lvn.UI.LvnScreenDirector.Current.Reset();
            // TabReset: глава всегда возвращает ленту на «Главную» — стартовая
            // четверть полотна (pan 0.35 в ShowMenuScene) обязана совпадать с
            // фактической вкладкой после выхода из главы.
            // Режим говорим ОДИН раз — Режиссёру (через бар, который и есть его
            // голос): кружок загрузок теперь слушает сам. Рассылка вручную
            // держалась ровно до третьего пути (показ хаба), где звали только
            // бар, и кружок оставался с игровым отступом поверх меню.
            OnChapterSessionStart += () => { ShowMenuChrome(); TabReset(); Lvn.UI.LvnScreenDirector.Current.AnnounceChapter(true); };
            OnChapterSessionEnd += () => { ShowMenuChrome(); Lvn.UI.LvnScreenDirector.Current.AnnounceChapter(false); };
            // Пилюли валют бар слушает сам (подписка в его конструкторе, снятие
            // — на уходе с панели). Здесь стояла подписка ЗА него: оболочка
            // знала, что бару нужно знать о деньгах.

            if (assets is CachingAssets ca)
            {
                DownloadHud = new Lvn.UI.Screens.DownloadHud();
                AddChrome(DownloadHud);
                _root.schedule.Execute(() =>
                {
                    DownloadHud.Tick(ca.Loader.Transfers());
                    // Safe area здесь больше не раздаётся: бар и кружок следят
                    // за кромкой сами (LvnEdges.Follow в их конструкторах).
                    // Хост мерил её раз в 300 мс и кормил двоих — то есть знал
                    // за них, где у экрана край.
                    TopBar.SyncTapZone(); // зона и декор уступают модали сцены
                    DownloadHud.SetSceneModal(
                        !(TopBar.TapZoneAvailable?.Invoke() ?? true));
                }).Every(300);
            }

            // Кошелёк → пилюли навбара: бар подписан на деньги сам. Здесь была
            // ВТОРАЯ подписка — в полосу GameHud, которую убрали 26.08 и
            // которая с тех пор ни разу не показывалась.
            _storeUi = ui.store;
        }

        private StoreConfig _storeUi;



        /// <summary>ONE store: every entry (quick menu, wallet "+", scripts'
        /// <c>ext store_show</c>, the hub) opens the pack shop.</summary>
        /// <summary>Прежнее имя магазина. НЕ ЗОВЁТСЯ внутри движка — оставлено
        /// для хостов, собранных до переименования: две двери в одну комнату
        /// дешевле сломанной сборки у того, кто взял библиотеку.</summary>
        public Task OpenStoreAsync(CancellationToken ct = default)
            => OpenPackShopAsync(ct);

        /// <summary>Open the app-level settings overlay (sound, language, account,
        /// version, socials, legal). Completes when the player closes it.</summary>
        public Task OpenSettingsAsync(CancellationToken ct = default)
            => ShowModalAsync(Settings, ct);

        /// <summary>Open the rich detail page for a title; returns true if the player
        /// pressed Play/Continue. Configure Detail's fields before calling.</summary>
        public Task<bool> OpenDetailAsync(CancellationToken ct = default)
            => ShowModalAsync(Detail, ct);
        // Галерея — со своим циклом (не LvnOverlayScreen): в стек роутера не
        // входит, но фон у неё свой и «назад» она обрабатывает сама.
        public Task OpenGalleryAsync(CancellationToken ct = default)
            => Gallery != null ? Gallery.ShowAsync(ct) : Task.CompletedTask;
        /// <summary>
        /// Показать профиль КАК ЕСТЬ — тем, что уже положено в его поля.
        ///
        /// <para>Внутри движка не зовётся, и намеренно: наш хост открывает
        /// профиль своим путём, который сперва собирает отношения из манифеста и
        /// состояния. Позвать эту дверь напрямую значит показать профиль без
        /// связей — не пустой экран, а ПРАВДОПОДОБНЫЙ, и разницу заметит только
        /// тот, кто знает, что связи должны быть.</para>
        ///
        /// <para>Оставлена для хостов, которые наполняют профиль сами: оболочка
        /// манифеста не видит и собрать связи не может.</para>
        /// </summary>
        public Task OpenProfileAsync(CancellationToken ct = default)
            => ShowModalAsync(Profile, ct);
        public Task OpenDailyAsync(CancellationToken ct = default)
            => ShowModalAsync(Daily, ct);
        public Task OpenLeaderboardAsync(CancellationToken ct = default)
            => ShowModalAsync(Leaderboard, ct);
        /// <summary>Витрина скинов. НЕ ПОДКЛЮЧЕНА: экран показывает выдуманный
        /// каталог (демо-вёрстка), и точки входа у него нет — гардероб ведёт
        /// свою торговлю сам. Дверь оставлена хосту, который приведёт витрину к
        /// настоящим данным.</summary>
        public Task OpenSkinShopAsync(CancellationToken ct = default)
            => ShowModalAsync(SkinShop, ct);
        /// <summary>Быстрый магазин — модаль со своим фоном (вкладка ленты —
        /// отдельный инстанс, её открывает TabGoTo(1)).</summary>
        public Task OpenPackShopAsync(CancellationToken ct = default)
            => ShowModalAsync(PackShopModal, ct);

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
        /// <summary>
        /// ЖИВОЕ ОБНОВЛЕНИЕ МЕНЯЕТ ДАННЫЕ, А НЕ ОФОРМЛЕНИЕ — и это граница, а не
        /// недоделка.
        ///
        /// <para>Состав новелл, коллекции, каталог гардероба приезжают сюда и
        /// заменяются на месте. А цвета, подписи и размеры экранов приходят из
        /// <c>manifest.ui</c> в КОНСТРУКТОРЫ: сменить их значит пересобрать
        /// экраны, то есть выбросить открытую карточку, набранный текст и
        /// прокрутку под пальцем игрока. Ради правки оттенка кнопки это дорого;
        /// новое оформление доедет со следующим запуском.</para>
        ///
        /// <para>Исключение — простые настройки БЕЗ вёрстки: их видно сразу и
        /// стоят они ничего.</para>
        /// </summary>
        public void ApplyLiveUpdate(LvnManifest manifest)
        {
            if (manifest == null) return;
            _manifest = manifest;
            Carousel?.SetTitles(manifest.titles);
            Hub?.SetData(manifest.collections, manifest.titles);
            // Вкладка гардероба тоже живёт манифестом (каталог нарядов, ростер
            // персонажей) — без этой строки она одна оставалась на прежнем
            // содержимом, пока соседние экраны показывали новое.
            WardrobeTab?.SetManifest(manifest);
            // Простые настройки без вёрстки — тоже данные: чем торгует
            // «бесплатная» карточка магазина, игра решает манифестом, и решение
            // обязано доезжать вместе с ним.
            if (PackShop != null) PackShop.AdPlacement = manifest.ui?.store?.ad_placement;
            if (PackShopModal != null) PackShopModal.AdPlacement = manifest.ui?.store?.ad_placement;
        }


        /// <summary>Вводная новелла, которую ещё не прошли, или null. Новелла
        /// объявляет себя вводной полем <c>type: "intro"</c> в манифесте — как и
        /// всякий другой вид новеллы, данными, а не кодом оболочки.</summary>
        /// <summary>Сессия главы началась/кончилась — для всего, что живёт
        /// ТОЛЬКО вне новеллы (музыка меню и т.п.): хост глушит на старте и
        /// возвращает по выходу в меню.</summary>
        public Action OnChapterSessionStart;
        public Action OnChapterSessionEnd;

        /// <summary>
        /// УБРАТЬ ИНТЕРФЕЙС МЕНЮ С КАДРА на время ухода в створ. Героиня
        /// растворяется на живой сцене, и кнопки поверх неё превратили бы уход
        /// в «экран закрылся». Уходит всё, что стоит между игроком и сценой:
        /// лента, навбар, кружок загрузок и слои страниц с попапами — «играть»
        /// жмут с открытой карточки новеллы, и переход играл бы ЗА ней.
        /// </summary>
        public void HideMenuChrome()
        {
            if (Hub != null) Hub.style.display = DisplayStyle.None;
            if (TopBar != null) TopBar.style.display = DisplayStyle.None;
            if (DownloadHud != null) DownloadHud.style.display = DisplayStyle.None;
            if (_tabsLayer != null) _tabsLayer.style.display = DisplayStyle.None;
            if (_popupLayer != null) _popupLayer.style.display = DisplayStyle.None;
        }

        /// <summary>
        /// ВЕРНУТЬ ИНТЕРФЕЙС МЕНЮ. Возвращает НЕ ВСЁ, что спрятал сосед выше, и
        /// это не забытая строка: хаб впускает Швейцар (<c>LvnUsher</c>) на
        /// своей итерации цикла — с выдержкой под вуалью бута и входной
        /// анимацией. Вернуть хаб здесь значит показать его дважды: сперва
        /// рывком из этого метода, потом ещё раз по-настоящему.
        ///
        /// <para>Асимметрия «спрятал пятерых, вернул четверых» выглядит багом
        /// ровно до этого абзаца — и однажды кто-нибудь «починит» её, получив
        /// мигание хаба на каждом возвращении из главы. Правило: у видимости
        /// хаба ОДИН хозяин, и это не хром.</para>
        /// </summary>
        public void ShowMenuChrome()
        {
            if (TopBar != null) TopBar.style.display = DisplayStyle.Flex;
            if (DownloadHud != null) DownloadHud.style.display = DisplayStyle.Flex;
            if (_tabsLayer != null) _tabsLayer.style.display = DisplayStyle.Flex;
            if (_popupLayer != null) _popupLayer.style.display = DisplayStyle.Flex;
        }

        private VisualElement _tabsLayer, _popupLayer;

        /// <summary>Игрок пошёл в главу: хост доигрывает уход в створ на сцене
        /// и только потом отдаёт кадр главе.</summary>
        public Func<Task> OnPortalEnter;
        /// <summary>Хаб показан на экране — хост ставит сцену меню (после
        /// всех уборок конца главы, а не до них).</summary>
        public Action OnMenuVisible;


        /// <summary>Диагностика «белого прямоугольника»: перечислить крупные
        /// СВЕТЛЫЕ и НЕПРОЗРАЧНЫЕ поверхности дерева оболочки. Пустой список
        /// значит, что светлое пятно рисует сцена (UGUI), а не оболочка.</summary>
        private void DumpOpaqueSurfaces()
        {
            if (_root == null) return;
            var sb = new System.Text.StringBuilder("[lvn-white] светлые поверхности оболочки:\n");
            int found = 0;
            _root.Query<VisualElement>().ForEach(el =>
            {
                if (el.resolvedStyle.display == DisplayStyle.None) return;
                var c = el.resolvedStyle.backgroundColor;
                if (c.a < 0.35f) return;
                if ((c.r + c.g + c.b) / 3f < 0.55f) return;   // светлое, а не «Полночь»
                var wb = el.worldBound;
                if (wb.width < 80f || wb.height < 80f) return; // крупное пятно, не чип
                found++;
                sb.AppendLine($"  <{el.GetType().Name}> name='{el.name}' "
                              + $"классы=[{string.Join(",", el.GetClasses())}] "
                              + $"rect=({wb.x:0},{wb.y:0} {wb.width:0}x{wb.height:0}) "
                              + $"цвет=#{ColorUtility.ToHtmlStringRGBA(c)} opacity={el.resolvedStyle.opacity:0.00}");
            });
            if (found == 0)
                sb.AppendLine("  — ничего светлого НЕ найдено: белое рисует сцена, а не оболочка");
            if (TopBar != null) sb.AppendLine($"  верхний бар: {TopBar.DebugState}");
            Debug.Log(sb.ToString());
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







    }
}


