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
    public sealed class BrowseHub : VisualElement
    {
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
        public string PlayerName;
        public int PlayerLevel;

        private VisualElement _topPills; // hub HUD: currency balances
        private Label _playerNameLabel, _playerLevelLabel;
        private readonly BrowseConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly Color _bg, _titleColor, _text, _dim, _card, _cardText, _accent, _accentText, _border;
        private readonly float _radius;
        private readonly LvnTheme _theme;

        private readonly VisualElement _hubView, _collectionView, _detailView;
        private VisualElement _bottomNav;
        private readonly Label _hubTitle, _hubSubtitle;
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
        private readonly List<Texture2D> _gradients = new List<Texture2D>(); // generated depth textures

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
            style.backgroundColor = _bg;
            // Атмосфера — под всё содержимое. У темы без неё не делает ничего.
            LvnBackdrop.Apply(this, _theme);
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
            profile.style.flexDirection = FlexDirection.Row;
            profile.style.alignItems = Align.Center;
            var avatar = IconButton(LvnIcon.Profile, 28f, _text, () => { if (OnMenu != null) _ = OnMenu(); });
            avatar.style.width = 56; avatar.style.height = 56;
            avatar.style.backgroundColor = _theme.SurfaceHi;
            avatar.style.marginRight = 12;
            avatar.style.borderTopWidth = 2; avatar.style.borderBottomWidth = 2;
            avatar.style.borderLeftWidth = 2; avatar.style.borderRightWidth = 2; SetBorderColor(avatar, _accent);
            LvnChrome.Round(avatar, _theme.RoundPills ? 28f : _radius);
            profile.Add(avatar);
            var nameCol = new VisualElement();
            _playerNameLabel = new Label(); _playerNameLabel.style.color = _text;
            _playerNameLabel.style.fontSize = 36; _playerNameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameCol.Add(_playerNameLabel);
            _playerLevelLabel = new Label(); _playerLevelLabel.style.color = _dim; _playerLevelLabel.style.fontSize = 26;
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
            var gift = IconButton(LvnIcon.Gift, 24f, _text, () => { if (OnDaily != null) _ = OnDaily(); });
            gift.style.width = 44; gift.style.height = 44; gift.style.marginLeft = 10;
            gift.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.ClearBorder(gift); LvnChrome.Round(gift, LvnTokens.RadiusSm);
            var dot = new Label { pickingMode = PickingMode.Ignore };
            dot.style.position = Position.Absolute; dot.style.top = 6; dot.style.right = 6;
            dot.style.width = 10; dot.style.height = 10; dot.style.backgroundColor = _accent; LvnChrome.Round(dot, 5f);
            gift.Add(dot);
            rightGroup.Add(gift);
            var gear = IconButton(LvnIcon.Settings, 24f, _dim, () => { if (OnMenu != null) _ = OnMenu(); });
            gear.style.width = 44; gear.style.height = 44; gear.style.marginLeft = 10;
            gear.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.ClearBorder(gear); LvnChrome.Round(gear, LvnTokens.RadiusSm);
            rightGroup.Add(gear);
            topBar.Add(rightGroup);
            _hubView.Add(topBar);

            var brand = new VisualElement();
            brand.style.marginTop = 2; brand.style.marginBottom = 20;
            var eyebrow = new Label((_cfg.subtitle ?? "Выбери путь").ToUpperInvariant());
            eyebrow.style.color = _accent; eyebrow.style.fontSize = 30;
            eyebrow.style.letterSpacing = 4f; eyebrow.style.unityFontStyleAndWeight = FontStyle.Bold;
            eyebrow.style.marginBottom = 8;
            brand.Add(eyebrow);
            _hubTitle = Heading(_cfg.title ?? "", 58);
            brand.Add(_hubTitle);
            _hubSubtitle = new Label(); // (kept for API; the eyebrow carries the sub-line)
            var rule = new VisualElement();
            rule.style.height = 3; rule.style.width = 44; rule.style.marginTop = 12;
            rule.style.backgroundColor = _accent; LvnChrome.Round(rule, 2f);
            brand.Add(rule);
            _hubView.Add(brand);
            _hubRows = new ScrollView(ScrollViewMode.Vertical);
            _hubRows.style.flexGrow = 1;
            _hubRows.verticalScrollerVisibility = ScrollerVisibility.Hidden; // clean app feel, no track/arrows
            _hubView.Add(_hubRows);
            _hubView.Add(BottomNav()); // Home / Store / Wardrobe / Profile
            Add(_hubView);

            // ── COLLECTION ──
            _collectionView = Column();
            _collectionView.Add(BackBar(out _collectionTitle, () => ShowHub()));
            _collectionList = new ScrollView(ScrollViewMode.Vertical);
            _collectionList.style.flexGrow = 1;
            _collectionList.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _collectionView.Add(_collectionList);
            Add(_collectionView);

            // ── DETAIL ──
            _detailView = Column();
            _detailView.Add(BackBar(out _detailTitle, BackFromDetail));
            _detailImage = new VisualElement { pickingMode = PickingMode.Ignore };
            _detailImage.style.height = Length.Percent(42);
            _detailImage.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
            LvnChrome.Round(_detailImage, _radius);
            Edge(_detailImage);
            _detailImage.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            _detailImage.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _detailImage.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _detailImage.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            _detailImage.style.marginBottom = 16;
            _detailImage.style.overflow = Overflow.Hidden;
            _detailImage.style.justifyContent = Justify.FlexEnd;
            // Затемнение под подписью: белый заголовок обязан читаться на любой
            // обложке, включая светлую.
            var dScrim = new VisualElement { pickingMode = PickingMode.Ignore };
            dScrim.style.position = Position.Absolute;
            dScrim.style.left = 0; dScrim.style.right = 0; dScrim.style.bottom = 0;
            dScrim.style.height = Length.Percent(55f);
            dScrim.style.backgroundImage = Gradient(new Color(0f, 0f, 0f, 0.02f), new Color(0f, 0f, 0f, 0.85f));
            _detailImage.Add(dScrim);
            // КРУПНОЕ название на самой обложке. Раньше имя новеллы жило только
            // в узкой строке возврата мелким кеглем, и экран открывался
            // безымянным: картинка, абзац и кнопка.
            var dCap = new VisualElement { pickingMode = PickingMode.Ignore };
            dCap.style.paddingLeft = 20; dCap.style.paddingRight = 20; dCap.style.paddingBottom = 16;
            _detailBigTitle = new Label(string.Empty);
            _detailBigTitle.style.color = _titleColor; _detailBigTitle.style.fontSize = 60;
            _detailBigTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _detailBigTitle.style.whiteSpace = WhiteSpace.Normal;
            _detailBigTitle.style.letterSpacing = _theme.Tracking;
            dCap.Add(_detailBigTitle);
            _detailSubtitle = new Label(string.Empty);
            _detailSubtitle.style.color = _dim; _detailSubtitle.style.fontSize = 30;
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
            _detailDesc.style.color = _text; _detailDesc.style.fontSize = 36;
            _detailDesc.style.whiteSpace = WhiteSpace.Normal;
            dBody.Add(_detailDesc);
            _detailView.Add(dBody);

            // Действие и цена в один ряд: стоимость рядом с кнопкой, а не
            // спрятана в её надписи.
            var dActions = new VisualElement();
            dActions.style.flexDirection = FlexDirection.Row;
            dActions.style.alignItems = Align.Center;
            dActions.style.marginTop = 14;
            _detailPlay = AccentButton(_cfg.play_text ?? "Играть", () => _ = PlayTappedAsync());
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

            // Keep the top-bar balances live with the wallet while on screen.
            RegisterCallback<AttachToPanelEvent>(_ => { Lvn.Services.LvnWallet.Changed += RefreshTopBar; RefreshTopBar(); });
            RegisterCallback<DetachFromPanelEvent>(_ => Lvn.Services.LvnWallet.Changed -= RefreshTopBar);

            // Safe-area: keep headers below the notch and the bottom nav above the
            // home indicator. Re-resolves whenever geometry changes.
            RegisterCallback<GeometryChangedEvent>(_ => ApplySafeArea());

            ShowHub();
        }

        // Notch / home-indicator insets: the hub keeps its bigger brand offset,
        // the sub-screens keep a smaller one, and the bottom nav grows a bottom pad.
        private void ApplySafeArea()
        {
            var insets = ScreenUi.SafeVerticalInsets(this);
            _hubView.style.paddingTop = Mathf.Max(52f, insets.x + 12f);
            _collectionView.style.paddingTop = Mathf.Max(28f, insets.x + 12f);
            _detailView.style.paddingTop = Mathf.Max(28f, insets.x + 12f);
            if (_bottomNav != null) _bottomNav.style.paddingBottom = 6f + insets.y;
        }

        // The two currencies, top-right, each with a "+" to buy (like every F2P
        // game). Energy shows N/cap while it's refilling.
        private void RefreshTopBar()
        {
            if (_topPills == null) return;
            if (_playerNameLabel != null) _playerNameLabel.text = string.IsNullOrEmpty(PlayerName) ? "Гость" : PlayerName;
            if (_playerLevelLabel != null) _playerLevelLabel.text = "Уровень " + (PlayerLevel > 0 ? PlayerLevel : 1);
            _topPills.Clear();
            _topPills.Add(CurrencyPill("energy", LvnIcon.Energy, _accent));
            _topPills.Add(CurrencyPill("gold", LvnIcon.Gem, _theme.Gold));
        }

        private VisualElement CurrencyPill(string currency, LvnIcon icon, Color iconColor)
        {
            long bal = Lvn.Services.LvnWallet.Balances.TryGetValue(currency, out var b) ? b : 0;
            string amount = (Lvn.Services.LvnWallet.Regen.TryGetValue(currency, out var r) && r.Cap > 0 && bal < r.Cap)
                ? bal + "/" + r.Cap : bal.ToString("N0");

            var pill = new VisualElement();
            pill.style.flexDirection = FlexDirection.Row;
            pill.style.alignItems = Align.Center;
            pill.style.marginLeft = 8;
            pill.style.backgroundColor = new Color(0f, 0f, 0f, 0.4f);
            pill.style.borderTopWidth = 1; pill.style.borderBottomWidth = 1;
            pill.style.borderLeftWidth = 1; pill.style.borderRightWidth = 1; SetBorderColor(pill, _border);
            LvnChrome.Round(pill, _theme.RoundPills ? 18f : _radius);
            pill.style.paddingLeft = 12; pill.style.paddingTop = 5; pill.style.paddingBottom = 5;

            var ic = LvnIcons.Make(icon, 20f, iconColor, 0f, _theme.IconGlow);
            ic.style.marginRight = 5;
            pill.Add(ic);
            var amt = new Label(amount); amt.style.color = _text; amt.style.fontSize = 33;
            amt.style.unityFontStyleAndWeight = FontStyle.Bold;
            pill.Add(amt);

            // the "+" — a small accent disc that opens the store
            var plus = new Label("+");
            plus.style.color = _accentText; plus.style.backgroundColor = _accent;
            plus.style.fontSize = 30; plus.style.unityFontStyleAndWeight = FontStyle.Bold;
            plus.style.unityTextAlign = TextAnchor.MiddleCenter;
            plus.style.width = 26; plus.style.height = 26; plus.style.marginLeft = 8;
            LvnChrome.Round(plus, 13f);
            pill.Add(plus);

            pill.RegisterCallback<ClickEvent>(evt => { if (OnStore != null) _ = OnStore(); });
            LvnMotion.Tappable(pill);
            return pill;
        }

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
            LvnAsync.Fire(Lvn.Services.LvnWallet.RefreshAsync(), "Refresh"); // fresh top-bar balances
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
            _collectionTitle.text = c.name ?? c.id;
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
            if (OnOpenDetail != null) { var target = t; _ = OpenDetailFlow(target); }
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
            _detailTitle.text = t.name ?? t.id;
            _detailBigTitle.text = _theme.Heading(t.name ?? t.id);
            var art = t.card;
            _detailSubtitle.text = t.subtitle ?? "";
            _detailSubtitle.style.display = string.IsNullOrEmpty(t.subtitle)
                ? DisplayStyle.None : DisplayStyle.Flex;
            _detailDesc.text = art?.description ?? t.subtitle ?? "";
            var img = art?.image ?? t.cover_url;
            if (!string.IsNullOrEmpty(img)) _ = ScreenUi.AssignBgAsync(_detailImage, img, _assets);
            bool locked = IsLocked(t);
            _detailPlay.SetEnabled(!locked);
            _detailPlay.text = locked ? (_cfg.locked_text ?? "Закрыто")
                : PlayLabel(t);
            _detailChips.Clear();
            if (locked) _detailChips.Add(Chip(null, _dim, LvnIcon.Lock));
            else if (t.cost != null && t.cost.amount > 0)
                _detailChips.Add(Chip(t.cost.amount.ToString(), _theme.Gold, LvnIcon.Energy));
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
            _detailPlay.SetEnabled(false);
            bool go = OnPlay == null || await OnPlay(t);
            _detailPlay.SetEnabled(true);
            if (go) _tcs?.TrySetResult(t);
        }

        // ── unlock ──────────────────────────────────────────────────────────────
        private bool IsLocked(LvnTitle t)
        {
            if (t == null || string.IsNullOrEmpty(t.unlock)) return false;
            try
            {
                var vars = new Dictionary<string, JToken> { ["global"] = _globalVars };
                return !Lvn.LvnExpression.EvaluateBool(t.unlock, vars);
            }
            catch { return false; } // a bad expression never bricks the hub
        }

        // ── builders ──────────────────────────────────────────────────────────────
        private void BuildHubTiles()
        {
            if (_hubRows == null) return;
            _hubRows.Clear();
            // Any title not curated into a collection (e.g. a freshly imported novel)
            // still shows — grouped into an auto "library" row so the hub reflects the
            // real content, not just the hand-authored shelves.
            var orphans = OrphanTitles();
            // Feature the title the player can CONTINUE, if any; else a recommended one.
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
                var lib = new LvnCollection { id = "_library", name = _cfg.library_text ?? "Новеллы", titles = orphans };
                var libRow = CollectionRow(lib, hero: _collections.Count == 0);
                if (libRow != null) _hubRows.Add(libRow);
            }
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
            b.style.height = 340; b.style.flexShrink = 0; b.style.marginBottom = 30;
            b.style.overflow = Overflow.Hidden;
            LvnChrome.Round(b, _radius + 2f);

            string art = t.card?.image ?? t.cover_url;
            if (!string.IsNullOrEmpty(art))
            {
                var img = new VisualElement { pickingMode = PickingMode.Ignore };
                ScreenUi.Stretch(img); img.style.backgroundColor = _card;
                img.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                img.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                img.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                img.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                b.Add(img); _ = ScreenUi.AssignBgAsync(img, art, _assets);
                var scrim = new VisualElement { pickingMode = PickingMode.Ignore };
                ScreenUi.Stretch(scrim);
                scrim.style.backgroundImage = Gradient(new Color(0f, 0f, 0f, 0.05f), new Color(0.03f, 0.01f, 0.03f, 0.92f));
                b.Add(scrim);
            }
            else if (_theme.AccentPlaceholders)
                b.style.backgroundImage = Gradient(Lighten(_accent, 0.05f), Darken(_accent, 0.55f));
            else
                b.style.backgroundImage = Gradient(Lighten(_card, 0.14f), Darken(_card, 0.35f));
            Edge(b);

            b.style.justifyContent = Justify.FlexEnd;
            b.style.paddingLeft = 22; b.style.paddingRight = 22; b.style.paddingBottom = 20;

            var eyebrow = new Label((resume ? (_cfg.continue_text ?? "Продолжить") : (_cfg.featured_text ?? "Рекомендуем")).ToUpperInvariant());
            eyebrow.style.color = _accent; eyebrow.style.fontSize = 24; eyebrow.style.letterSpacing = 3f;
            eyebrow.style.unityFontStyleAndWeight = FontStyle.Bold; eyebrow.style.marginBottom = 6;
            b.Add(eyebrow);
            var title = new Label(t.name ?? t.id);
            title.style.color = _text; title.style.fontSize = 57; title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.whiteSpace = WhiteSpace.Normal; b.Add(title);

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row; actions.style.alignItems = Align.Center;
            actions.style.marginTop = 12;
            var play = new Button(() => { if (locked) { _ = OnLockedHint?.Invoke(t.name ?? t.id, t.locked_hint ?? ""); } else OpenDetail(t, CurrentCollectionOf(t)); })
            { text = locked ? (_cfg.locked_text ?? "Закрыто") : (resume ? (_cfg.continue_text ?? "Продолжить") : (_cfg.play_text ?? "Играть")) };
            play.style.fontSize = 36; play.style.paddingLeft = 26; play.style.paddingRight = 26;
            play.style.paddingTop = 12; play.style.paddingBottom = 12;
            play.style.color = _accentText; play.style.backgroundColor = _accent;
            LvnChrome.ClearBorder(play); LvnChrome.Round(play, LvnTokens.RadiusSm);
            actions.Add(play);
            if (!locked && t.cost != null && t.cost.amount > 0)
            {
                var chip = Chip(t.cost.amount.ToString(), _theme.Gold, LvnIcon.Energy); chip.style.marginLeft = 12;
                actions.Add(chip);
            }
            b.Add(actions);
            return b;
        }

        // ── bottom nav ──
        private VisualElement BottomNav()
        {
            var nav = new VisualElement();
            _bottomNav = nav;
            nav.style.flexDirection = FlexDirection.Row;
            nav.style.alignItems = Align.Stretch;
            nav.style.flexShrink = 0;
            nav.style.paddingBottom = 8; nav.style.paddingTop = 8;
            nav.style.borderTopWidth = _theme.EdgeWidth > 0f ? _theme.EdgeWidth : 1f;
            nav.style.borderTopColor = _theme.EdgeWidth > 0f ? _theme.EdgeColor : _border;
            // Панель непрозрачна: под ней проезжает лента, и полупрозрачный низ
            // превращается в кашу из букв.
            nav.style.backgroundColor = new Color(_bg.r, _bg.g, _bg.b, 0.96f);
            // Callbacks are read LAZILY at click time — the host wires them AFTER
            // this is built, so capturing the field value here would capture null.
            nav.Add(NavTab(LvnIcon.Home, _cfg.nav_home ?? "Главная", true, null));
            nav.Add(NavTab(LvnIcon.Store, _cfg.nav_store ?? "Магазин", false, () => { if (OnStore != null) _ = OnStore(); }));
            nav.Add(NavTab(LvnIcon.Wardrobe, _cfg.nav_wardrobe ?? "Гардероб", false, () => { if (OnWardrobe != null) _ = OnWardrobe(); }));
            nav.Add(NavTab(LvnIcon.Gallery, _cfg.nav_gallery ?? "Галерея", false, () => { if (OnGallery != null) _ = OnGallery(); }));
            nav.Add(NavTab(LvnIcon.Profile, _cfg.nav_profile ?? "Профиль", false, () => { if (OnProfile != null) _ = OnProfile(); }));
            return nav;
        }

        private VisualElement NavTab(LvnIcon icon, string label, bool active, System.Action onTap)
        {
            var tab = new VisualElement();
            // РАВНЫЕ ДОЛИ, а не распределение по содержимому. Раньше здесь стояло
            // justify-content: space-around при вкладках разной ширины — и
            // «Главная» с «Гардеробом» разъезжались тем сильнее, чем длиннее
            // слово. Одинаковый flex-basis выравнивает центры, а центры и есть
            // то, по чему глаз читает ряд как ряд.
            tab.style.flexGrow = 1; tab.style.flexBasis = 0;
            tab.style.alignItems = Align.Center;
            tab.style.justifyContent = Justify.FlexStart;
            tab.style.paddingTop = 6; tab.style.paddingBottom = 6;
            var color = active ? _accent : _dim;

            // Активную вкладку помечает ЧЕРТА СВЕРХУ, а не только цвет: черта
            // читается боковым зрением и не теряется у тех, кто не различает
            // акцент и приглушённый на глаз.
            var mark = new VisualElement { pickingMode = PickingMode.Ignore };
            mark.style.height = 3; mark.style.width = 26;
            mark.style.backgroundColor = active ? _accent : Color.clear;
            mark.style.marginBottom = 6;
            tab.Add(mark);

            tab.Add(LvnIcons.Make(icon, 30f, color, 0f, active ? _theme.IconGlow : 0f));
            var lb = new Label(_theme.Heading(label)) { pickingMode = PickingMode.Ignore };
            lb.style.fontSize = 26; lb.style.color = color; lb.style.marginTop = 5;
            lb.style.letterSpacing = _theme.Tracking;
            lb.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
            tab.Add(lb);
            if (onTap != null) { tab.AddManipulator(new Clickable(onTap)); LvnMotion.Tappable(tab); }
            return tab;
        }

        // Staggered fade+rise entrance for the feed's rows — the premium feel.
        private void AnimateIn(VisualElement container)
        {
            int i = 0;
            foreach (var child in container.Children())
            {
                var el = child;
                el.style.opacity = 0f;
                el.style.translate = new Translate(0, 20);
                int delay = 30 + i * 65;
                i++;
                el.schedule.Execute(() =>
                    el.experimental.animation.Start(0f, 1f, 340, (e, v) =>
                    {
                        e.style.opacity = v;
                        e.style.translate = new Translate(0, (1f - v) * 20f);
                    }).Ease(UnityEngine.UIElements.Experimental.Easing.OutCubic)
                ).ExecuteLater(delay);
            }
        }

        // One collection as a streaming-style row: a header (name + "Все →") over
        // a horizontal slider of title cards. "Все →" opens the full list; a card
        // (or its "Подробнее") opens the detail.
        private VisualElement CollectionRow(LvnCollection c, bool hero)
        {
            var row = new VisualElement();
            row.style.flexShrink = 0; // children of a vertical ScrollView must not shrink
            row.style.marginBottom = 30;

            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;
            head.style.justifyContent = Justify.SpaceBetween;
            head.style.marginBottom = 14;
            var title = new Label(_theme.Heading(c.name ?? c.id));
            title.style.color = _text; title.style.fontSize = 54;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.letterSpacing = _theme.Tracking;
            head.Add(title);
            // «Все ›» — подпись и векторная стрелка. Стрелка символом «→» на
            // части шрифтов Android тоже отсутствует, а её пропажу замечаешь
            // позже прочих: пустое место в конце строки читается как отступ.
            var all = new VisualElement();
            all.style.flexDirection = FlexDirection.Row;
            all.style.alignItems = Align.Center;
            var allText = new Label(_theme.Heading(_cfg.all_text ?? "Все")) { pickingMode = PickingMode.Ignore };
            allText.style.color = _accent; allText.style.fontSize = 36;
            allText.style.unityFontStyleAndWeight = FontStyle.Bold;
            allText.style.letterSpacing = _theme.Tracking;
            all.Add(allText);
            var allArrow = LvnIcons.Make(LvnIcon.Chevron, 20f, _accent, 0f, _theme.IconGlow);
            allArrow.style.marginLeft = 4;
            all.Add(allArrow);
            all.RegisterCallback<ClickEvent>(_ => ShowCollection(c));
            LvnMotion.Tappable(all);
            head.Add(all);
            row.Add(head);

            var strip = new ScrollView(ScrollViewMode.Horizontal);
            strip.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            // И ВЕРТИКАЛЬНУЮ тоже. Полоса брала своё не от прокрутки, а от того,
            // что карточка выше отведённой ей строки: сбоку появлялся системный
            // ползунок чужого вида, а низ карточки обрезался.
            strip.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            strip.style.flexShrink = 0;
            strip.style.flexDirection = FlexDirection.Row;
            var entering = new System.Collections.Generic.List<VisualElement>();
            if (c.titles != null)
                foreach (var id in c.titles)
                    if (_titles.TryGetValue(id, out var t))
                    {
                        var card = SliderCard(t, c, hero);
                        strip.Add(card);
                        entering.Add(card);
                    }
            // Карточки приезжают со сдвигом, а не разом: одновременное появление
            // читается как перерисовка экрана, последовательное — как намерение.
            // Пустой сборник — не строка нулевой высоты, а ОТСУТСТВИЕ строки.
            // Фиксированная высота ниже иначе зарезервировала бы полэкрана
            // пустоты под заголовком, который не о чем.
            if (entering.Count == 0) return null;
            // Высота считается от карточки, а не подбирается: постер 320,
            // отступ 12, название в две строки ~62, подпись ~26. Ставится
            // только теперь, когда точно известно, что карточки есть.
            strip.style.height = 320f + 12f + 62f + 26f;
            Lvn.UI.LvnMotion.Stagger(entering);
            row.Add(strip);
            return row;
        }

        // A poster card inside a slider: gradient depth, a cost/lock chip top-right,
        // the title + a "Подробнее" button at the bottom. Whole card opens detail.
        // A Spotify-style shelf card: a rounded poster on top, the title and a
        // dimmed subtitle below it (no overlaid text, no "more" button — the whole
        // card is the tap target).
        private VisualElement SliderCard(LvnTitle t, LvnCollection from, bool hero)
        {
            bool locked = IsLocked(t);
            var card = new VisualElement();
            card.style.width = 250;
            card.style.flexShrink = 0;      // horizontal slider: keep the poster size
            card.style.marginRight = 18;
            card.style.opacity = locked ? 0.5f : 1f;

            // poster — rounded, art fills it (portrait crop reads best for character art)
            var poster = new VisualElement();
            poster.style.width = Length.Percent(100f);
            poster.style.height = 320;
            poster.style.overflow = Overflow.Hidden;
            poster.style.backgroundColor = _card;
            LvnChrome.Round(poster, _radius);

            string art = t.card?.image ?? t.cover_url;
            if (!string.IsNullOrEmpty(art))
            {
                var img = new VisualElement { pickingMode = PickingMode.Ignore };
                ScreenUi.Stretch(img);
                img.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                img.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                img.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                img.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                poster.Add(img);
                LvnAsync.Fire(ScreenUi.AssignBgAsync(img, art, _assets), "AssignBg");
            }
            else
            {
                poster.style.backgroundImage = (hero && _theme.AccentPlaceholders)
                    ? Gradient(Lighten(_accent, 0.04f), Darken(_accent, 0.5f))
                    : Gradient(Lighten(_card, 0.12f), Darken(_card, 0.3f));
            }
            // Кромка идёт НА ЛЮБУЮ обложку, а не только на заглушку. Сначала я
            // повесил её лишь туда, где картинки нет, — и на живом каталоге
            // подпись темы исчезла: обложки лежали голыми прямоугольниками, а
            // светилась только пустота. Рамка вокруг кадра — то, что делает его
            // частью интерфейса, а не картинкой, положенной сверху.
            Edge(poster);

            // cost / lock chip, small, floated on the poster
            var chip = locked ? Chip(_cfg.locked_text, _dim, LvnIcon.Lock)
                : (t.cost != null && t.cost.amount > 0 ? Chip(t.cost.amount.ToString(), _theme.Gold, LvnIcon.Energy) : null);
            if (chip != null)
            {
                chip.style.position = Position.Absolute; chip.style.top = 12; chip.style.right = 12;
                poster.Add(chip);
            }
            card.Add(poster);

            // title + subtitle, below the poster (Spotify-style)
            var name = new Label(t.name ?? t.id);
            name.style.color = _text; name.style.fontSize = 36;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.whiteSpace = WhiteSpace.Normal;
            name.style.marginTop = 12;
            name.style.maxHeight = 62;      // две строки: третья вытолкнула бы подпись
            name.style.overflow = Overflow.Hidden;
            card.Add(name);

            string sub = t.subtitle ?? t.card?.description;
            if (!string.IsNullOrEmpty(sub))
            {
                var subLbl = new Label(sub);
                subLbl.style.color = _dim; subLbl.style.fontSize = 26; subLbl.style.marginTop = 4;
                subLbl.style.whiteSpace = WhiteSpace.NoWrap;
                subLbl.style.overflow = Overflow.Hidden;
                subLbl.style.textOverflow = TextOverflow.Ellipsis;
                card.Add(subLbl);
            }

            LvnMotion.Tappable(card);
            card.RegisterCallback<ClickEvent>(evt =>
            {
                if (locked) { _ = OnLockedHint?.Invoke(t.name ?? t.id, t.locked_hint ?? ""); }
                else OpenDetail(t, from);
            });
            return card;
        }

        private static void SetBorderColor(VisualElement el, Color c)
        {
            el.style.borderTopColor = c; el.style.borderBottomColor = c;
            el.style.borderLeftColor = c; el.style.borderRightColor = c;
        }

        // A full-width list card (one per row): a thumbnail on the left, then the
        // name + a mini-description + a progress bar, and a cost/lock chip.
        private VisualElement TitleCard(LvnTitle t)
        {
            bool locked = IsLocked(t);
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.height = 128;
            card.style.backgroundColor = _card;
            card.style.borderTopWidth = 1; card.style.borderBottomWidth = 1;
            card.style.borderLeftWidth = 1; card.style.borderRightWidth = 1;
            SetBorderColor(card, _border);
            card.style.opacity = locked ? 0.55f : 1f;
            LvnChrome.Round(card, _radius);
            card.style.marginBottom = 14;
            card.style.overflow = Overflow.Hidden;

            // thumbnail (left)
            var thumb = new VisualElement { pickingMode = PickingMode.Ignore };
            thumb.style.width = 128; thumb.style.height = Length.Percent(100f);
            thumb.style.backgroundColor = _theme.SurfaceHi;
            Edge(thumb);
            thumb.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            thumb.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            thumb.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            thumb.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            var art = t.card?.image ?? t.cover_url;
            if (!string.IsNullOrEmpty(art)) _ = ScreenUi.AssignBgAsync(thumb, art, _assets);
            card.Add(thumb);

            // text column (right)
            var col = new VisualElement();
            col.style.flexGrow = 1; col.style.justifyContent = Justify.Center;
            col.style.paddingLeft = 18; col.style.paddingRight = 16;
            col.style.paddingTop = 14; col.style.paddingBottom = 14;

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row; top.style.justifyContent = Justify.SpaceBetween;
            top.style.alignItems = Align.Center;
            var name = new Label(t.name ?? t.id);
            name.style.color = _text; name.style.fontSize = 36;
            name.style.unityFontStyleAndWeight = FontStyle.Bold; name.style.flexGrow = 1;
            top.Add(name);
            if (locked) top.Add(Chip(_cfg.locked_text, _dim, LvnIcon.Lock));
            else if (t.cost != null && t.cost.amount > 0) top.Add(Chip(t.cost.amount.ToString(), _theme.Gold, LvnIcon.Energy));
            col.Add(top);

            var desc = new Label(t.card?.description ?? t.subtitle ?? "");
            desc.style.color = _dim; desc.style.fontSize = 24; desc.style.marginTop = 5;
            desc.style.whiteSpace = WhiteSpace.Normal;
            desc.style.overflow = Overflow.Hidden;
            col.Add(desc);

            // a thin progress bar (fallback demo progress)
            var track = new VisualElement();
            track.style.height = 6; track.style.marginTop = 10; track.style.flexShrink = 0;
            track.style.backgroundColor = _theme.SurfaceHi; LvnChrome.Round(track, 3f); track.style.overflow = Overflow.Hidden;
            var fill = new VisualElement();
            fill.style.height = Length.Percent(100f);
            fill.style.width = Length.Percent(locked ? 0f : 35f); // demo progress
            fill.style.backgroundColor = _accent; LvnChrome.Round(fill, 3f);
            track.Add(fill); col.Add(track);

            card.Add(col);

            LvnMotion.Tappable(card);
            card.RegisterCallback<ClickEvent>(evt =>
            {
                if (locked) { _ = OnLockedHint?.Invoke(t.name ?? t.id, t.locked_hint ?? ""); }
                else OpenDetail(t, CurrentCollectionOf(t));
            });
            return card;
        }

        private LvnCollection CurrentCollectionOf(LvnTitle t)
        {
            foreach (var c in _collections)
                if (c.titles != null && c.titles.Contains(t.id)) return c;
            return null;
        }

        private string PlayLabel(LvnTitle t) =>
            t.cost != null && t.cost.amount > 0
                ? (_cfg.play_text ?? "Играть") + "  ·  " + string.Format(_cfg.cost_text ?? "{0}", t.cost.amount)
                : (_cfg.play_text ?? "Играть");

        /// <summary>
        /// Круглая/квадратная кнопка с векторной иконкой вместо надписи.
        ///
        /// <para>Текст у кнопки пустой намеренно: иконка — отдельный ребёнок, и
        /// потому её цвет, толщина линии и свечение живут своей жизнью, а не
        /// наследуются от стиля текста, у которого для этого нет свойств.</para>
        /// </summary>
        private Button IconButton(LvnIcon icon, float size, Color color, System.Action onTap)
        {
            var b = new Button(onTap) { text = "" };
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            b.style.paddingLeft = 0; b.style.paddingRight = 0;
            b.style.paddingTop = 0; b.style.paddingBottom = 0;
            b.Add(LvnIcons.Make(icon, size, color, 0f, _theme.IconGlow));
            return b;
        }

        /// <summary>Метка на карточке: иконка, подпись или и то и другое.
        /// Пустой текст — иконка одна, и метка сжимается до неё.</summary>
        private VisualElement Chip(string text, Color color, LvnIcon icon = LvnIcon.None)
        {
            var chip = new VisualElement();
            chip.style.flexDirection = FlexDirection.Row;
            chip.style.alignItems = Align.Center;
            chip.style.backgroundColor = new Color(0f, 0f, 0f, 0.28f);
            chip.style.paddingLeft = 10; chip.style.paddingRight = 10;
            chip.style.paddingTop = 4; chip.style.paddingBottom = 4;
            LvnChrome.Round(chip, 10f);
            if (icon != LvnIcon.None)
            {
                var ic = LvnIcons.Make(icon, 18f, color, 0f, _theme.IconGlow);
                if (!string.IsNullOrEmpty(text)) ic.style.marginRight = 5;
                chip.Add(ic);
            }
            if (!string.IsNullOrEmpty(text))
            {
                var lb = new Label(text) { pickingMode = PickingMode.Ignore };
                lb.style.color = color; lb.style.fontSize = 30;
                chip.Add(lb);
            }
            return chip;
        }

        // ── shared layout bits ──
        private VisualElement Column()
        {
            var col = new VisualElement();
            ScreenUi.Stretch(col);
            col.style.flexDirection = FlexDirection.Column;
            col.style.paddingTop = 28; col.style.paddingBottom = 24;
            col.style.paddingLeft = 30; col.style.paddingRight = 30;
            return col;
        }

        private VisualElement BackBar(out Label title, System.Action onBack)
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.marginBottom = 14;
            var back = new Button(onBack) { text = _cfg.back_text ?? "‹" };
            back.style.fontSize = 48; back.style.minWidth = 52;
            back.style.color = _titleColor;
            back.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f);
            LvnChrome.ClearBorder(back); LvnChrome.Round(back, _radius);
            bar.Add(back);
            title = Heading("", 30);
            title.style.marginLeft = 12;
            bar.Add(title);
            return bar;
        }

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
            b.style.fontSize = 42;
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
        private StyleBackground Gradient(Color top, Color bottom)
        {
            const int h = 128;
            var tex = new Texture2D(1, h, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < h; y++)
                tex.SetPixel(0, y, Color.Lerp(bottom, top, (float)y / (h - 1)));
            tex.Apply();
            _gradients.Add(tex);
            return new StyleBackground(Background.FromTexture2D(tex));
        }

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
            LvnMotion.SlideIn(target, 26f);
        }

        /// <summary>Светящаяся кромка по контуру — подпись технической темы.
        /// У темы без неё (EdgeWidth = 0) не делает ничего, поэтому вызывать
        /// можно безусловно.</summary>
        private void Edge(VisualElement el, float strength = 1f)
            => LvnChrome.Edge(el, strength);
    }
}
