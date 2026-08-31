using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The hub browse flow (an alternative to <see cref="TitleCarousel"/>, selected
    /// by <c>ui.browse.layout = "hub"</c>): three themeable screens —
    ///   1. HUB      — the app title + a tile per collection (Expeditions/Dates/…),
    ///   2. COLLECTION — the collection's titles as cards,
    ///   3. DETAIL   — a title's image + description + Play.
    /// Locked titles (their <c>unlock</c> expression over the player's
    /// <c>global.*</c> stats is false) show a lock badge and explain themselves on
    /// tap; Play runs the host's <see cref="OnPlay"/> gate (energy cost / store)
    /// and only then resolves. <see cref="PickTitleAsync"/> returns the chosen,
    /// unlocked, paid-for title (or null if cancelled).
    /// </summary>
    public sealed partial class BrowseHub : VisualElement, ILvnEntrance, Lvn.UI.ILvnRedress
    {
        /// <summary>Служебный сборник «всё, что не разложено по полкам». Его id
        /// не приходит из манифеста — по нему подпись узнаёт, что название
        /// полосы надо спросить у словаря, а не у данных.</summary>
        internal const string LibraryId = "_library";

        /// <summary>Loads the player's global stat flags (the <c>__global</c> blob)
        /// so <c>unlock</c> conditions can be evaluated. Null → everything unlocked.</summary>
        public System.Func<Task<JObject>> GlobalStatsProvider;
        /// <summary>The Play gate: charge the entry cost / confirm. Returns true to
        /// launch. Null → always launch (free).</summary>
        public System.Func<LvnTitle, Task<bool>> OnPlay;
        /// <summary>Show a message when a locked card is tapped. Null → silent.</summary>
        public System.Func<string, string, Task> OnLockedHint;
        /// <summary>The avatar / account button (top-left). Null → no button.</summary>
        public System.Func<Task> OnMenu;
        /// <summary>Tapping a currency pill's "+" (top-right) → open the store.</summary>
        public System.Func<Task> OnStore;
        /// <summary>The wardrobe tab in the bottom nav. Null → the tab still shows
        /// (a fallback slot) but does nothing.</summary>
        public System.Func<Task> OnWardrobe;
        /// <summary>Gallery / Profile nav tabs.</summary>
        public System.Func<Task> OnGallery;
        public System.Func<Task> OnProfile;
        /// <summary>The 🎁 daily-rewards button (top bar). Null → hidden.</summary>
        public System.Func<Task> OnDaily;
        /// <summary>Open the rich detail page for a title; returns true if the player
        /// pressed Play. Null → falls back to the built-in inline detail view.</summary>
        public System.Func<LvnTitle, Task<bool>> OnOpenDetail;
        /// <summary>Player display name + level for the top bar (fallbacks used
        /// when unset — filled with real data later).</summary>
        // ИМЯ ИГРОКА ЗДЕСЬ НЕ ХРАНИТСЯ. Поле было копией того, что лежит в
        // настройках устройства, и обновлять его приходилось руками из двух
        // мест оболочки: после ввода ника и при открытии профиля. Забудь один
        // вызов — и хаб показывает вчерашнее имя. Спрашиваем у роли.
        public int PlayerLevel;

        private VisualElement _topPills; // hub HUD: currency balances
        private VisualElement _profileBlock; // аватар+имя — скрыт при едином навбаре
        private VisualElement _settingsBtn;  // шестерёнка — скрыта при едином навбаре
        private Label _playerNameLabel, _playerLevelLabel;
        private readonly BrowseConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly Color _bg, _titleColor, _text, _dim, _card, _cardText, _accent, _accentText, _border;
        private readonly float _radius;
        private readonly LvnTheme _theme;

        private readonly VisualElement _hubView, _collectionView, _detailView;
        private VisualElement _bottomNav;
        private readonly Label _hubTitle, _hubSubtitle;
        private Label _hubEyebrow;

        // Надзаголовок хаба собирается в одном месте: он стоит над названием
        // игры и меняется вместе с языком, как всё остальное.
        private string HubEyebrow()
            => LvnWords.Pick("hub.subtitle", _cfg.subtitle, "Choose your path").ToUpperInvariant();
        private readonly ScrollView _hubRows; // vertical stack of per-collection sliders
        private readonly Label _collectionTitle;
        private readonly ScrollView _collectionList;
        private readonly VisualElement _detailImage;
        private readonly Label _detailTitle, _detailDesc, _detailBigTitle, _detailSubtitle;
        private readonly VisualElement _detailChips;
        private readonly Button _detailPlay;

        private readonly Dictionary<string, LvnTitle> _titles = new Dictionary<string, LvnTitle>();
        private List<LvnCollection> _collections = new List<LvnCollection>();
        private JObject _globalVars = new JObject(); // cached flags for unlock eval
        private LvnTitle _detailTarget;

        private TaskCompletionSource<LvnTitle> _tcs;

        public BrowseHub(BrowseConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new BrowseConfig();
            _assets = assets;
            // Тема идёт ПЕРВОЙ и служит запасным значением для всех цветов ниже.
            // Порядок именно такой: точечная настройка из манифеста сильнее
            // темы, тема сильнее умолчаний движка. Поэтому «ui.browse.theme =
            // cyber» перекрашивает хаб целиком, а отдельный accent_color рядом
            // с ней продолжает работать и переопределяет только себя.
            _theme = LvnTheme.ByName(_cfg.theme);
            LvnTheme.Use(_theme);
            _bg = UiColor.Parse(_cfg.bg_color, _theme.Bg);
            _titleColor = UiColor.Parse(_cfg.title_color, _theme.Text);
            _text = UiColor.Parse(_cfg.text_color, _theme.Text);
            _dim = UiColor.Parse(_cfg.dim_text_color, _theme.TextDim);
            _card = UiColor.Parse(_cfg.card_color, _theme.Surface);
            _cardText = UiColor.Parse(_cfg.card_text_color, _theme.Text);
            _accent = UiColor.Parse(_cfg.accent_color, _theme.Accent);
            _accentText = UiColor.Parse(_cfg.accent_text_color, _theme.OnAccent);
            _border = _theme.Border;
            _radius = _cfg.card_radius ?? _theme.Radius;

            ScreenUi.Stretch(this);
            // Фон и атмосфера живут НА КОРНЕ ОБОЛОЧКИ (решение Ильи 26.08:
            // один параллакс-фон на все экраны меню) — хаб прозрачен.
            style.backgroundColor = Color.clear;
            // Хаб — верх ленты вкладок (его нижнее меню поверх разделов):
            // корень не должен глотать тапы по магазину/профилю под собой.
            pickingMode = PickingMode.Ignore;
            // Отклик на нажатие — на весь хаб разом. Раньше он стоял ровно на
            // карточках ленты, и всё остальное (вкладки, плашки, «+») на палец
            // не отвечало: экран читался как картинка.
            LvnMotion.EnableTapFeedback(this);

            // ── HUB ── a brand block up top, then full-bleed collection cards
            // that fill the height. Cards get texture gradients for real depth
            // (UITK inline styles can't do gradients/shadows any other way).
            _hubView = Column();
            _hubView.style.paddingTop = 52; // clear the status bar / notch
            // Мягкое свечение сверху — но ТОЛЬКО если тема не принесла своего
            // фона: сплошной градиент во весь экран закрыл бы собой и сетку, и
            // виньетку, то есть ровно то, ради чего тему включали.
            if (!_theme.Glow && !_theme.Grid)
                _hubView.style.backgroundImage = Gradient(Color.Lerp(_bg, _accent, 0.16f), _bg);

            // Standard mobile-game top bar: player avatar + name/level on the left,
            // currency balances (with a "+" to buy) and settings on the right.
            var topBar = new VisualElement();
            topBar.style.flexDirection = FlexDirection.Row;
            topBar.style.alignItems = Align.Center;
            topBar.style.justifyContent = Justify.SpaceBetween;
            topBar.style.marginBottom = 22;

            var profile = new VisualElement();
            _profileBlock = profile;
            profile.style.flexDirection = FlexDirection.Row;
            profile.style.alignItems = Align.Center;
            var avatar = IconButton(LvnIcon.Profile, 28f, _text, () => { if (OnMenu != null) LvnAsync.Fire(OnMenu(), "OpenMenu"); });
            avatar.style.width = 56; avatar.style.height = 56;
            avatar.style.backgroundColor = _theme.SurfaceHi;
            avatar.style.marginRight = 12;
            LvnChrome.Border(avatar, _accent, 2f);
            LvnChrome.Round(avatar, _theme.RoundPills ? 28f : _radius);
            profile.Add(avatar);
            var nameCol = new VisualElement();
            _playerNameLabel = new Label(); _playerNameLabel.style.color = _text;
            _playerNameLabel.style.fontSize = Lvn.UI.LvnFonts.Size(36f); _playerNameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameCol.Add(_playerNameLabel);
            _playerLevelLabel = new Label(); _playerLevelLabel.style.color = _dim; _playerLevelLabel.style.fontSize = Lvn.UI.LvnFonts.Size(26f);
            if (!(_cfg.show_level ?? true)) _playerLevelLabel.style.display = DisplayStyle.None;
            nameCol.Add(_playerLevelLabel);
            profile.Add(nameCol);
            topBar.Add(profile);

            var rightGroup = new VisualElement();
            rightGroup.style.flexDirection = FlexDirection.Row;
            rightGroup.style.alignItems = Align.Center;
            _topPills = new VisualElement();
            _topPills.style.flexDirection = FlexDirection.Row;
            _topPills.style.alignItems = Align.Center;
            rightGroup.Add(_topPills);
            // daily-rewards gift (badge dot hints there's something to claim)
            var gift = IconButton(LvnIcon.Gift, 24f, _text, () => { if (OnDaily != null) LvnAsync.Fire(OnDaily(), "OpenDaily"); });
            gift.style.width = 44; gift.style.height = 44; gift.style.marginLeft = 10;
            gift.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.ClearBorder(gift); LvnChrome.Round(gift, LvnTokens.RadiusSm);
            var dot = new Label { pickingMode = PickingMode.Ignore };
            dot.style.position = Position.Absolute; dot.style.top = 6; dot.style.right = 6;
            dot.style.width = 10; dot.style.height = 10; dot.style.backgroundColor = _accent; LvnChrome.Round(dot, 5f);
            gift.Add(dot);
            // Чистка витрины (TR-25): партнёр убирает ежедневную награду данными.
            if (!(_cfg.show_daily ?? true)) gift.style.display = DisplayStyle.None;
            rightGroup.Add(gift);
            var gear = IconButton(LvnIcon.Settings, 24f, _dim, () => { if (OnMenu != null) LvnAsync.Fire(OnMenu(), "OpenMenu"); });
            _settingsBtn = gear;
            gear.style.width = 44; gear.style.height = 44; gear.style.marginLeft = 10;
            gear.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.ClearBorder(gear); LvnChrome.Round(gear, LvnTokens.RadiusSm);
            rightGroup.Add(gear);
            topBar.Add(rightGroup);
            _hubView.Add(topBar);

            var brand = new VisualElement();
            brand.style.marginTop = 2; brand.style.marginBottom = 20;
            _hubEyebrow = new Label(HubEyebrow());
            var eyebrow = _hubEyebrow;
            eyebrow.style.color = _accent; eyebrow.style.fontSize = Lvn.UI.LvnFonts.Size(30f);
            eyebrow.style.letterSpacing = 4f; eyebrow.style.unityFontStyleAndWeight = FontStyle.Bold;
            eyebrow.style.marginBottom = 8;
            brand.Add(eyebrow);
            _hubTitle = Heading(LvnWords.Pick("browse.title", _cfg.title, ""), 58);
            brand.Add(_hubTitle);
            _hubSubtitle = new Label(); // (kept for API; the eyebrow carries the sub-line)
            var rule = new VisualElement();
            rule.style.height = 3; rule.style.width = 44; rule.style.marginTop = 12;
            rule.style.backgroundColor = _accent; LvnChrome.Round(rule, 2f);
            brand.Add(rule);
            _hubView.Add(brand);
            _hubRows = Lvn.UI.LvnScroll.Vertical();
            _hubRows.style.flexGrow = 1;
            // Контент ленты ПРИЖАТ К НИЗУ В УПОР (Илья 27.08): контейнер
            // скролла минимум во весь вьюпорт — воздух-растяжка сверху (см.
            // BuildHubTiles) отжимает ряды к нижнему меню, а не оставляет
            // пустоту под ними.
            _hubRows.contentContainer.style.minHeight = Length.Percent(100f);
            _hubView.Add(_hubRows);
            // Нижнее меню — В КОРНЕ хаба, не в контенте: контент уезжает
            // лентой вкладок, а меню стоит поверх разделов и переключает их
            // (живой скрин «в магазине нижнего меню нету»).
            var navRoot = BottomNav();
            navRoot.style.position = Position.Absolute;
            navRoot.style.left = 0; navRoot.style.right = 0; navRoot.style.bottom = 0;
            Add(navRoot);
            _hubView.style.paddingBottom = 124; // лента не ныряет под меню
            Add(_hubView);

            // ── COLLECTION ──
            _collectionView = Column();
            _collectionView.Add(BackBar(out _collectionTitle, () => ShowHub()));
            _collectionList = Lvn.UI.LvnScroll.Vertical();
            _collectionList.style.flexGrow = 1;
            _collectionView.Add(_collectionList);
            Add(_collectionView);

            // ── DETAIL ──
            _detailView = Column();
            _detailView.Add(BackBar(out _detailTitle, BackFromDetail));
            Lvn.UI.LvnRedress.Bind(_detailTitle, () => _detailTarget == null ? string.Empty
                : LvnWords.Name("title", _detailTarget.id, _detailTarget.name));
            _detailImage = new VisualElement { pickingMode = PickingMode.Ignore };
            _detailImage.style.height = Length.Percent(42);
            _detailImage.style.backgroundColor = LvnTokens.Veil(0.35f);
            LvnChrome.Round(_detailImage, _radius);
            Edge(_detailImage);
            LvnPicture.Fit(_detailImage);
            _detailImage.style.marginBottom = 16;
            _detailImage.style.overflow = Overflow.Hidden;
            _detailImage.style.justifyContent = Justify.FlexEnd;
            // Затемнение под подписью: белый заголовок обязан читаться на любой
            // обложке, включая светлую.
            var dScrim = new VisualElement { pickingMode = PickingMode.Ignore };
            dScrim.style.position = Position.Absolute;
            dScrim.style.left = 0; dScrim.style.right = 0; dScrim.style.bottom = 0;
            dScrim.style.height = Length.Percent(55f);
            dScrim.style.backgroundImage = Gradient(LvnTokens.Veil(0.02f), LvnTokens.Veil(0.85f));
            _detailImage.Add(dScrim);
            // КРУПНОЕ название на самой обложке. Раньше имя новеллы жило только
            // в узкой строке возврата мелким кеглем, и экран открывался
            // безымянным: картинка, абзац и кнопка.
            var dCap = new VisualElement { pickingMode = PickingMode.Ignore };
            dCap.style.paddingLeft = 20; dCap.style.paddingRight = 20; dCap.style.paddingBottom = 16;
            _detailBigTitle = Lvn.UI.LvnRedress.Bind(new Label(), () => _detailTarget == null ? string.Empty
                : _theme.Heading(LvnWords.Name("title", _detailTarget.id, _detailTarget.name)));
            _detailBigTitle.style.color = _titleColor; _detailBigTitle.style.fontSize = Lvn.UI.LvnFonts.Size(60f);
            _detailBigTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _detailBigTitle.style.whiteSpace = WhiteSpace.Normal;
            _detailBigTitle.style.letterSpacing = _theme.Tracking;
            dCap.Add(_detailBigTitle);
            _detailSubtitle = Lvn.UI.LvnRedress.Bind(new Label(), () => _detailTarget == null ? string.Empty
                : LvnWords.Name("subtitle", _detailTarget.id, _detailTarget.subtitle ?? ""));
            _detailSubtitle.style.color = _dim; _detailSubtitle.style.fontSize = Lvn.UI.LvnFonts.Size(30f);
            _detailSubtitle.style.whiteSpace = WhiteSpace.Normal;
            _detailSubtitle.style.marginTop = 4;
            dCap.Add(_detailSubtitle);
            _detailImage.Add(dCap);
            _detailView.Add(_detailImage);

            // Описание — В ПАНЕЛИ, а не голым абзацем на фоне. Пустое место под
            // коротким текстом тогда читается как поле панели, а не как дыра.
            var dBody = new VisualElement();
            dBody.style.flexGrow = 1;
            dBody.style.backgroundColor = _card;
            dBody.style.paddingLeft = 18; dBody.style.paddingRight = 18;
            dBody.style.paddingTop = 16; dBody.style.paddingBottom = 16;
            LvnChrome.Round(dBody, _radius);
            Edge(dBody, 0.7f);
            _detailDesc = new Label(string.Empty);
            _detailDesc.style.color = _text; _detailDesc.style.fontSize = Lvn.UI.LvnFonts.Size(36f);
            _detailDesc.style.whiteSpace = WhiteSpace.Normal;
            dBody.Add(_detailDesc);
            _detailView.Add(dBody);

            // Действие и цена в один ряд: стоимость рядом с кнопкой, а не
            // спрятана в её надписи.
            var dActions = new VisualElement();
            dActions.style.flexDirection = FlexDirection.Row;
            dActions.style.alignItems = Align.Center;
            dActions.style.marginTop = 14;
            // ПОДПИСЬ КНОПКИ ЗАВИСИТ ОТ ДВУХ ВЕЩЕЙ СРАЗУ: от языка и от того,
            // какую новеллу открыли (её цена, её замок). Держать оба ответа
            // порознь — присваивание при открытии и привязку для языка — значит
            // получить расхождение ровно в тот момент, когда игрок переключает
            // язык, стоя на карточке. Источник один, экран лишь просит
            // перечитать его (см. ShowDetail).
            _detailPlay = AccentButton(string.Empty, () => LvnAsync.Fire(PlayTappedAsync(), "PlayTapped"));
            Lvn.UI.LvnRedress.Bind(_detailPlay, () =>
            {
                var t = _detailTarget;
                if (t == null) return LvnWords.Pick("hub.play", _cfg?.play_text, "Play");
                return IsLocked(t) ? LvnWords.Pick("hub.locked", _cfg?.locked_text, "Locked") : PlayLabel(t);
            });
            _detailPlay.style.flexGrow = 1;
            _detailPlay.style.marginTop = 0;
            dActions.Add(_detailPlay);
            _detailChips = new VisualElement();
            _detailChips.style.flexDirection = FlexDirection.Row;
            _detailChips.style.alignItems = Align.Center;
            _detailChips.style.marginLeft = 12;
            dActions.Add(_detailChips);
            _detailView.Add(dActions);
            Add(_detailView);
            // Меню создано раньше вьюх — поднять НАД ними, иначе полноразмерный
            // _hubView глотает клики по нему (живой скрин «меню не кликается»).
            _bottomNav.BringToFront();

            // Балансы в шапке живы, пока хаб на экране, — связку держит Поводок.
            Lvn.LvnLeash.WhileOnScreen(this,
                () => Lvn.Services.LvnWallet.Changed += RefreshTopBar,
                () => Lvn.Services.LvnWallet.Changed -= RefreshTopBar,
                RefreshTopBar);

            // Кромка: ЧИСЛА и ПОВОД пересчитать держит Кромочник. Здесь стояла
            // подписка на одну лишь смену геометрии — а комментарий рядом уже
            // утверждал, что поворот экрана эту формулу будит. Не будил:
            // поворот UITK-событием не сопровождается.
            LvnEdges.Follow(this, _ => ApplySafeArea());

            ShowHub();
        }

        // Вырез камеры и домашняя полоса: числа и повод пересчитать держит
        // КРОМОЧНИК (LvnEdges) — хаб только говорит, какая поверхность чем
        // является: главная с крупной шапкой, внутренние страницы, нижняя
        // панель. Раньше здесь стояли свои Max(52,…)/Max(28,…)/+6, а ловился
        // только GeometryChanged: поворот экрана эту формулу не будил.
        private void ApplySafeArea()
        {
            _hubView.style.paddingTop =
                LvnEdges.Top(this, LvnEdges.HomeTopMin, LvnEdges.PageTopAir);
            _collectionView.style.paddingTop =
                LvnEdges.Top(this, LvnEdges.PageTopMin, LvnEdges.PageTopAir);
            _detailView.style.paddingTop =
                LvnEdges.Top(this, LvnEdges.PageTopMin, LvnEdges.PageTopAir);
            if (_bottomNav != null)
                _bottomNav.style.paddingBottom = LvnEdges.Bottom(this, LvnEdges.NavBottomAir);
        }

        // The two currencies, top-right, each with a "+" to buy (like every F2P
        // game). Energy shows N/cap while it's refilling.
        private void RefreshTopBar()
        {
            if (_topPills == null) return;
            if (_profileBlock != null)
                _profileBlock.style.display = ExternalTopBar ? DisplayStyle.None : DisplayStyle.Flex;
            if (_settingsBtn != null)
                _settingsBtn.style.display = ExternalTopBar ? DisplayStyle.None : DisplayStyle.Flex;
            // Подпись безымянного игрока — у роли: здесь стояло русское слово
            // прямо в движке, и другая игра получала его насильно.
            if (_playerNameLabel != null)
                _playerNameLabel.text = Lvn.UI.LvnPlayerName.Display;
            // Через СЛОВАРЬ (ключ тот же, что у профиля): здесь слово стояло
            // строкой — вторая надпись про одно и то же, и переводились бы они
            // порознь.
            if (_playerLevelLabel != null)
                _playerLevelLabel.text = LvnWords.Of("profile.level", "Level {0}",
                    PlayerLevel > 0 ? PlayerLevel : 1);

            // ПИЛЮЛИ НЕ ПЕРЕСОБИРАЮТСЯ НА КАЖДОЕ СОБЫТИЕ. Раньше здесь стоял
            // Clear() и полная сборка заново — с новой загрузкой значков. Вход
            // в хаб дёргает LvnWallet.RefreshAsync(), ответ приходит через
            // секунду, и шапка на ровном месте перерисовывалась у игрока на
            // глазах. Пилюля умеет обновлять своё число сама (LvnWalletPill
            // тикает раз в секунду), поэтому событие кошелька — это Refresh,
            // а не пересборка. Собираем заново, только если сменился САМ СПИСОК
            // валют или шапку выключили/включили.
            var want = ExternalTopBar ? EmptyCurrencies : Currencies;
            if (!SameCurrencies(_pillsFor, want))
            {
                _topPills.Clear();
                _pills.Clear();
                foreach (var cur in want)
                {
                    var pill = CurrencyPill(cur);
                    _pills.Add(pill);
                    _topPills.Add(pill);
                }
                _pillsFor = new List<string>(want);
                return;
            }
            foreach (var pill in _pills) pill.Refresh();
        }

        /// <summary>Единый навбар приложения несёт валюты сам — пилюли хаба
        /// выключаются, чтобы не дублировать (решение Ильи 26.08).</summary>
        public bool ExternalTopBar;

        /// <summary>Валюты шапки, по порядку (ui.browse.currencies). Дефолт —
        /// прежняя пара; хост подменяет данными манифеста. «gold» был зашит в
        /// код, и у игры с валютой «crystals» шапка вечно показывала ноль.</summary>
        public List<string> Currencies = new List<string> { "energy", "gold" };

        // Плашка кошелька — общий элемент оболочки (LvnWalletPill). Своя копия
        // жила здесь пятой по счёту: свой формат числа, свой выбор значка по
        // «currency == "energy"», свой «плюс» лейблом вместо кнопки. Осталась
        // только метрика шапки хаба.
        // Плашка кошелька — общий элемент оболочки (LvnWalletPill), общими же
        // значениями: своя копия жила здесь пятой по счёту и отличалась всем
        // сразу — форматом числа, выбором значка по «currency == "energy"»,
        // кеглем 33 и «плюсом» из лейбла. Кошелёк должен выглядеть одинаково
        // везде, где его показывают.
        // Что сейчас висит в шапке: список валют, под который пилюли собраны,
        // и сами пилюли — чтобы обновлять их, а не рождать заново.
        private List<string> _pillsFor;
        private readonly List<LvnWalletPill> _pills = new List<LvnWalletPill>();
        private static readonly List<string> EmptyCurrencies = new List<string>();

        private static bool SameCurrencies(List<string> a, IReadOnlyList<string> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], System.StringComparison.Ordinal)) return false;
            return true;
        }

        private LvnWalletPill CurrencyPill(string currency)
            => new LvnWalletPill(currency, new LvnWalletPill.Look
            {
                MarginLeft = 8,
                Radius = _theme.RoundPills ? 18f : _radius,
                Edge = true,
                Background = LvnTokens.Veil(0.40f),
                TextColor = _text,
            }, _assets,
            onTap: OnStore != null ? () => LvnAsync.Fire(OnStore(), "OpenStore") : (System.Action)null,
            onPlus: OnStore != null ? () => LvnAsync.Fire(OnStore(), "OpenStore") : (System.Action)null);

        public void SetData(List<LvnCollection> collections, List<LvnTitle> titles)
        {
            _titles.Clear();
            if (titles != null)
                foreach (var t in titles)
                    if (t != null && !string.IsNullOrEmpty(t.id))
                        _titles[t.id] = t;
            _collections = collections ?? new List<LvnCollection>();
            BuildHubTiles();
        }

        /// <summary>Run the hub flow; resolves with the chosen title (unlocked and
        /// paid via <see cref="OnPlay"/>), or null if the player never picks one.</summary>
        public async Task<LvnTitle> PickTitleAsync(CancellationToken ct = default)
        {
            _globalVars = (GlobalStatsProvider != null ? await GlobalStatsProvider() : null) ?? new JObject();
            LvnAsync.Fire(Lvn.Services.LvnWallet.NudgeAsync(), "Refresh"); // свежие балансы навбара
            RefreshTopBar();
            ShowHub();
            BuildHubTiles(); // refresh lock states against the latest flags
            _tcs = new TaskCompletionSource<LvnTitle>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs?.TrySetResult(null));
            return await _tcs.Task;
        }

        // ── navigation ──────────────────────────────────────────────────────────
        private void ShowHub()
        {
            ShowView(_hubView);
        }

        private void ShowCollection(LvnCollection c)
        {
            // Имя подборки — через словарь, как и всё, что видит игрок:
            // авторское «Экспедиции» посреди английского хаба читается как
            // недоделанный перевод, а не как выбор.
            _collectionTitle.text = LvnWords.Name("collection", c.id, c.name);
            _collectionList.Clear();
            if (c.titles != null)
                foreach (var id in c.titles)
                    if (_titles.TryGetValue(id, out var t))
                        _collectionList.Add(TitleCard(t));
            ShowView(_collectionView);
        }

        // Prefer the host's rich detail page (TitleDetailScreen); fall back to the
        // built-in inline detail view when no host is wired.
        private void OpenDetail(LvnTitle t, LvnCollection from)
        {
            if (OnOpenDetail != null) { var target = t; Lvn.LvnAsync.Fire(OpenDetailFlow(target), "OpenDetail"); }
            else ShowDetail(t, from);
        }

        private async Task OpenDetailFlow(LvnTitle t)
        {
            bool play = await OnOpenDetail(t);
            if (play && (OnPlay == null || await OnPlay(t))) _tcs?.TrySetResult(t);
        }

        private LvnCollection _detailFrom;
        private void ShowDetail(LvnTitle t, LvnCollection from)
        {
            _detailTarget = t;
            _detailFrom = from;
            // Подписи детали привязаны к ОТКРЫТОЙ новелле (см. конструктор):
            // просим перечитать их, а не пишем текст поверх. Иначе смена языка
            // на открытой карточке возвращала бы прежние слова.
            Lvn.UI.LvnRedress.Refresh(_detailTitle);
            Lvn.UI.LvnRedress.Refresh(_detailBigTitle);
            Lvn.UI.LvnRedress.Refresh(_detailSubtitle);
            var art = t.card;
            _detailSubtitle.style.display = string.IsNullOrEmpty(t.subtitle)
                ? DisplayStyle.None : DisplayStyle.Flex;
            _detailDesc.text = art?.description ?? t.subtitle ?? "";
            var img = art?.image ?? t.CardArt();
            if (!string.IsNullOrEmpty(img)) LvnPicture.Photo(_detailImage, img, _assets);
            bool locked = IsLocked(t);
            _detailPlay.SetEnabled(!locked);
            Lvn.UI.LvnRedress.Refresh(_detailPlay);
            _detailChips.Clear();
            if (locked) _detailChips.Add(Chip(null, _dim, LvnIcon.Lock));
            else if (t.cost != null && t.cost.amount > 0)
                _detailChips.Add(CostChip(t.cost));
            ShowView(_detailView);
        }

        private void BackFromDetail()
        {
            if (_detailFrom != null) ShowCollection(_detailFrom);
            else ShowHub();
        }

        private async Task PlayTappedAsync()
        {
            var t = _detailTarget;
            if (t == null || IsLocked(t)) return;
            // Через дом занятости: вход в главу ждёт сеть и ассеты, и сорванное
            // ожидание оставляло «Играть» выключенной навсегда — на главном пути.
            bool go = false;
            await Lvn.UI.LvnBusy.RunAsync(_detailPlay,
                async () => { go = OnPlay == null || await OnPlay(t); },
                busyText: null, what: "Play");
            if (go) _tcs?.TrySetResult(t);
        }

        // ── unlock ──────────────────────────────────────────────────────────────
        // Подсказка «почему закрыто» — задача, и она под присмотром; хук
        // может быть не подключён (хост без подсказок), поэтому проверка на
        // null здесь, а не в трёх местах вызова.
        private void FireLockedHint(string name, string hint)
        {
            if (OnLockedHint == null) return;
            LvnAsync.Fire(OnLockedHint(name, hint), "LockedHint");
        }

        // Закрыта ли новелла, решает ПРИВРАТНИК; хаб только приносит ему
        // кросс-новелльные статы, которые сам же и кэширует.
        private bool IsLocked(LvnTitle t) => Lvn.Content.LvnGatekeeper.TitleLocked(t, _globalVars);

        /// <summary>Вертикальный скролл ленты — атмосфера оболочки читает его
        /// для параллакса.</summary>
        public float ScrollY => _hubRows != null ? _hubRows.scrollOffset.y : 0f;

        /// <summary>Тап «Главная» в нижнем меню — хост закрывает открытую
        /// вкладку-раздел (магазин/профиль) и возвращает ленту домой.</summary>
        public System.Action OnHomeNav;

        /// <summary>Контент-страница хаба для навигатора ленты (нижнее меню —
        /// отдельный слой в корне хаба и никуда не едет).</summary>
        public VisualElement ContentRoot => _hubView;

        // ── builders ──────────────────────────────────────────────────────────────
        // Отпечаток того, ЧТО лента показывает: сборники с их составом,
        // одиночные новеллы, витринная и её кнопка, плюс замки (они зависят от
        // глобальных статов, а те приезжают по сети). Совпал — пересобирать
        // нечего.
        private string TilesStamp()
        {
            var sb = new System.Text.StringBuilder();
            var resume = ResumableTitle();
            sb.Append(resume?.id).Append('|');
            sb.Append((ResumableTitle() ?? FirstTitle())?.id).Append('|');
            foreach (var c in _collections)
            {
                sb.Append(c?.id).Append(':');
                if (c?.titles != null)
                    foreach (var id in c.titles)
                    {
                        sb.Append(id);
                        if (_titles.TryGetValue(id, out var t)) sb.Append(IsLocked(t) ? '#' : '.');
                        sb.Append(',');
                    }
                sb.Append(';');
            }
            foreach (var id in OrphanTitles())
            {
                sb.Append(id);
                if (_titles.TryGetValue(id, out var t)) sb.Append(IsLocked(t) ? '#' : '.');
                sb.Append(',');
            }
            return sb.ToString();
        }
        private string _tilesStamp;

        private void BuildHubTiles()
        {
            if (_hubRows == null) return;
            // ЛЕНТА НЕ ПЕРЕСОБИРАЕТСЯ ВПУСТУЮ. Вход в хаб звал сборку дважды
            // (SetData и следом PickTitleAsync — «обновить замки по свежим
            // флагам»), и вторая сборка не просто повторяла работу: она заново
            // проигрывала ВХОДНУЮ АНИМАЦИЮ по уже видимому контенту. Игрок
            // видел, как хаб разок мигает на ровном месте.
            var stamp = TilesStamp();
            if (_tilesStamp == stamp) return;
            _tilesStamp = stamp;
            _hubRows.Clear();
            // Any title not curated into a collection (e.g. a freshly imported novel)
            // still shows — grouped into an auto "library" row so the hub reflects the
            // real content, not just the hand-authored shelves.
            var orphans = OrphanTitles();
            // Feature the title the player can CONTINUE, if any; else a recommended one.
            // Воздух сверху (Илья: «главную вниз, как гардероб») — лента
            // стартует под героиней и скроллится поверх неё. РАСТЯЖКА, а не
            // фикс: при коротком контенте воздух добирает всё свободное место
            // и прижимает ряды вниз В УПОР к нижнему меню (Илья 27.08); при
            // длинном — сжимается до минимума в 30%.
            var air = new VisualElement { pickingMode = PickingMode.Ignore };
            air.style.minHeight = Length.Percent(30f);
            air.style.flexGrow = 1;
            air.style.flexShrink = 0;
            _hubRows.Add(air);
            var resume = ResumableTitle();
            var featured = resume ?? FirstTitle();
            if (featured == null && orphans.Count > 0) _titles.TryGetValue(orphans[0], out featured);
            if (featured != null) _hubRows.Add(FeaturedBanner(featured, resume != null));
            for (int i = 0; i < _collections.Count; i++)
            {
                var cr = CollectionRow(_collections[i], hero: i == 0);
                if (cr != null) _hubRows.Add(cr);   // null = в сборнике нечего показывать
            }
            if (orphans.Count > 0)
            {
                // ИМЕНИ У СЛУЖЕБНОГО СБОРНИКА НЕТ НАРОЧНО. Оно вычислялось
                // здесь строкой и застывало в модели: смена языка полосу
                // «Новеллы» не доставала — данные пересобирает не переодевание,
                // а перезаход в хаб. Как зовётся эта полоса, знает подпись
                // (см. CollectionRow), и знает по ключу.
                var lib = new LvnCollection { id = LibraryId, titles = orphans };
                var libRow = CollectionRow(lib, hero: _collections.Count == 0);
                if (libRow != null) _hubRows.Add(libRow);
            }
            // Последний ряд — вплотную к нижнему меню: его штатная маржа 30px
            // оставляла зазор под «упором».
            var cc = _hubRows.contentContainer;
            if (cc.childCount > 0) cc[cc.childCount - 1].style.marginBottom = 0;
            AnimateIn(_hubRows); // staggered entrance
        }

        // Titles present in the manifest but not referenced by any collection —
        // preserves manifest order (dictionary order follows insertion in SetData).
        private List<string> OrphanTitles()
        {
            var inCol = new HashSet<string>();
            foreach (var c in _collections)
                if (c.titles != null)
                    foreach (var id in c.titles) inCol.Add(id);
            var orphans = new List<string>();
            foreach (var kv in _titles)
                if (!inCol.Contains(kv.Key)) orphans.Add(kv.Key);
            return orphans;
        }

        private LvnTitle FirstTitle()
        {
            foreach (var c in _collections)
                if (c.titles != null)
                    foreach (var id in c.titles)
                        if (_titles.TryGetValue(id, out var t)) return t;
            return null;
        }

        // The first title the player has an in-progress save for (LvnProgress) — the
        // "Продолжить" candidate for the featured banner. Null if nothing to resume.
        private LvnTitle ResumableTitle()
        {
            foreach (var c in _collections)
                if (c.titles != null)
                    foreach (var id in c.titles)
                        if (_titles.TryGetValue(id, out var t) && !IsLocked(t) && LvnProgress.Current(t) != null)
                            return t;
            return null;
        }

        // A large featured hero at the top of the feed — a recommended title with
        // its art, a Play button and the cost. Fallback: the first title.
        private VisualElement FeaturedBanner(LvnTitle t, bool resume = false)
        {
            bool locked = IsLocked(t);
            var b = new VisualElement();
            b.style.height = 370; b.style.flexShrink = 0; b.style.marginBottom = 30;
            b.style.overflow = Overflow.Hidden;
            LvnChrome.Round(b, _radius + 2f);

            string art = t.CardArt();
            if (!string.IsNullOrEmpty(art))
            {
                var img = new VisualElement { pickingMode = PickingMode.Ignore };
                ScreenUi.Stretch(img); img.style.backgroundColor = _card;
                b.Add(img); LvnPicture.Photo(img, art, _assets);
                var scrim = new VisualElement { pickingMode = PickingMode.Ignore };
                ScreenUi.Stretch(scrim);
                scrim.style.backgroundImage = Gradient(new Color(0f, 0f, 0f, 0.05f), new Color(0.03f, 0.01f, 0.03f, 0.92f));
                b.Add(scrim);
            }
            else b.style.backgroundImage = PosterFallbackImage(useAccent: true);
            // У витринного кадра есть тонкая рамка, но не тяжёлая неоновая
            // обводка: контраст должен остаться у одной кнопки «Играть».
            LvnChrome.Border(b, new Color(_accent.r, _accent.g, _accent.b, 0.52f), 1f);

            b.style.justifyContent = Justify.FlexEnd;
            b.style.paddingLeft = 24; b.style.paddingRight = 24; b.style.paddingBottom = 24;

            bool res0 = resume;
            var eyebrow = Lvn.UI.LvnRedress.Bind(new Label(), () =>
                (res0 ? LvnWords.Pick("hub.continue", _cfg.continue_text, "Continue")
                      : LvnWords.Pick("hub.featured", _cfg.featured_text, "Featured")).ToUpperInvariant());
            eyebrow.style.color = _accent; eyebrow.style.fontSize = Lvn.UI.LvnFonts.Size(24f); eyebrow.style.letterSpacing = 3f;
            eyebrow.style.unityFontStyleAndWeight = FontStyle.Bold; eyebrow.style.marginBottom = 6;
            b.Add(eyebrow);
            var title = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Name("title", t.id, t.name));
            title.style.color = _text; title.style.fontSize = Lvn.UI.LvnFonts.Size(57f); title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.whiteSpace = WhiteSpace.Normal; b.Add(title);

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row; actions.style.alignItems = Align.Center;
            actions.style.marginTop = 12;
            var play = new Button(() => { if (locked) { FireLockedHint(LvnWords.Name("title", t.id, t.name), t.locked_hint ?? ""); } else OpenDetail(t, CurrentCollectionOf(t)); })
            { };
            bool lock0 = locked, res1 = resume;
            Lvn.UI.LvnRedress.Bind(play, () =>
                lock0 ? LvnWords.Pick("hub.locked", _cfg.locked_text, "Locked")
                      : res1 ? LvnWords.Pick("hub.continue", _cfg.continue_text, "Continue")
                             : LvnWords.Pick("hub.play", _cfg.play_text, "Play"));
            play.style.fontSize = Lvn.UI.LvnFonts.Size(36f); play.style.paddingLeft = 26; play.style.paddingRight = 26;
            play.style.paddingTop = 12; play.style.paddingBottom = 12;
            play.style.color = _accentText; play.style.backgroundColor = _accent;
            LvnChrome.ClearBorder(play); LvnChrome.Round(play, LvnTokens.RadiusSm);
            actions.Add(play);
            if (!locked && t.cost != null && t.cost.amount > 0)
            {
                var chip = CostChip(t.cost); chip.style.marginLeft = 12;
                actions.Add(chip);
            }
            b.Add(actions);
            return b;
        }










        // One collection as a streaming-style row: a header (name + "Все →") over
        // a horizontal slider of title cards. "Все →" opens the full list; a card
        // (or its "Подробнее") opens the detail.
        private LvnCollection CurrentCollectionOf(LvnTitle t)
        {
            foreach (var c in _collections)
                if (c.titles != null && c.titles.Contains(t.id)) return c;
            return null;
        }

        private string PlayLabel(LvnTitle t) =>
            t.cost != null && t.cost.amount > 0
                ? (LvnWords.Pick("hub.play", _cfg.play_text, "Play")) + "  ·  " + string.Format(_cfg.cost_text ?? "{0}", t.cost.amount)
                : (LvnWords.Pick("hub.play", _cfg.play_text, "Play"));

        /// <summary>
        /// Круглая/квадратная кнопка с векторной иконкой вместо надписи.
        ///
        /// <para>Текст у кнопки пустой намеренно: иконка — отдельный ребёнок, и
        /// потому её цвет, толщина линии и свечение живут своей жизнью, а не
        /// наследуются от стиля текста, у которого для этого нет свойств.</para>
        /// </summary>
        private Label Heading(string text, int size)
        {
            var l = new Label(_theme.Heading(text));
            l.style.color = _titleColor; l.style.fontSize = size;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.letterSpacing = _theme.Tracking;
            // ПЕРЕНОС, а не обрезка. Длинное название («ELEMENTAL CHRONICLES»)
            // при запрете переноса уезжало за правый край и теряло последние
            // буквы — причём молча, так что на экране это читалось как опечатка
            // в манифесте, а не как переполнение.
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        private Button AccentButton(string text, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.fontSize = Lvn.UI.LvnFonts.Size(42f);
            b.style.marginTop = 14;
            b.style.paddingTop = 14; b.style.paddingBottom = 14;
            b.style.color = _accentText;
            b.style.backgroundColor = _accent;
            LvnChrome.ClearBorder(b); LvnChrome.Round(b, _radius);
            return b;
        }

        // A vertical gradient as a StyleBackground — the only way to get real
        // depth in UITK from code (no box-shadow / css-gradient on inline styles).
        // top = the upper edge colour, bottom = the lower edge.
        // Градиент рисует ФОН (LvnBackdrop): здесь на каждый вызов рождалась
        // новая текстура и оседала в списке, который никто не освобождал —
        // заглушка постера считается на каждую карточку без арта, а сетка
        // пересобирается на каждую смену данных, языка и шрифта.
        private StyleBackground Gradient(Color top, Color bottom)
            => Lvn.UI.LvnBackdrop.Vertical(top, bottom);

        /// <summary>
        /// КАК ВЫГЛЯДИТ НОВЕЛЛА БЕЗ АРТА — один ответ на весь хаб.
        ///
        /// <para>Заглушка постера рисовалась двумя местами: витриной и
        /// карточкой. Правило одно («градиент из акцента, а если тема этого не
        /// хочет — из цвета карточки»), но числа разошлись: 0.05/0.55 против
        /// 0.04/0.5 и 0.14/0.35 против 0.12/0.3. Разница мизерная и именно
        /// поэтому опасная — её не видно глазом, но она означает, что правила
        /// два, и следующая правка попадёт в одно из них.</para>
        ///
        /// <para>Числа взяты от витрины: крупный кадр задаёт вид, мелкая
        /// карточка его повторяет, а не наоборот.</para>
        /// </summary>
        private StyleBackground PosterFallbackImage(bool useAccent)
            => useAccent && _theme.AccentPlaceholders
                ? Gradient(Lighten(_accent, 0.05f), Darken(_accent, 0.55f))
                : Gradient(Lighten(_card, 0.14f), Darken(_card, 0.35f));

        private static Color Lighten(Color c, float a) => Color.Lerp(c, Color.white, a);
        private static Color Darken(Color c, float a) => Color.Lerp(c, Color.black, a);

        /// <summary>
        /// Переключение между тремя видами хаба — с движением, а не подменой.
        ///
        /// <para>Мгновенная смена display читается как перерисовка: непонятно,
        /// «вглубь» ты пошёл или «назад», и экран ощущается набором картинок.
        /// Короткий въезд снизу с проявлением стоит четверть секунды и отвечает
        /// на этот вопрос сам.</para>
        /// </summary>
        private void ShowView(VisualElement target)
        {
            foreach (var v in new[] { _hubView, _collectionView, _detailView })
                if (v != null) v.style.display = ReferenceEquals(v, target)
                    ? DisplayStyle.Flex : DisplayStyle.None;
            // ЗАРЯЖЕННЫЙ ВХОД СИЛЬНЕЕ ОБЫЧНОГО ПОКАЗА. FadeIn проявляет вид за
            // 160 мс и попутно обнуляет translate («хвост прежних въездов») —
            // на старте это выдавало готовое меню ДО его входа, а следом
            // приходил вход и уводил его влево: «повисело и резко исчезло»
            // (Илья 28.08). Пока экран заряжен, вид только переключается;
            // показывать его будет вход, и он же снимет заряд.
            if (_entranceArmed && ReferenceEquals(target, _hubView)) return;
            LvnMotion.FadeIn(target); // без въезда снизу — только проявление
        }

        /// <summary>Светящаяся кромка по контуру — подпись технической темы.
        /// У темы без неё (EdgeWidth = 0) не делает ничего, поэтому вызывать
        /// можно безусловно.</summary>
        private void Edge(VisualElement el, float strength = 1f)
            => LvnChrome.Edge(el, strength);
    }
}
