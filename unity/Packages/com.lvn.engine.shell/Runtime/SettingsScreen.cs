using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The app-level settings overlay: master sound switch, language, player id
    /// with a copy button, account/sign-in status, app version, social links and
    /// Terms/Privacy. Themed from <see cref="SettingsConfig"/> (manifest
    /// <c>ui.settings</c>). Distinct from the quick-menu's in-game settings panel
    /// (playback tweaks) — this is the standalone screen with account + legal.
    ///
    /// TCS-gated like <see cref="StoreScreen"/>: <see cref="ShowAsync"/> resolves
    /// when the player closes it. Sound/language write straight to
    /// <see cref="LvnPrefs"/> (the stage reacts live); links open through the
    /// <see cref="LvnWebView"/> seam; "Sign in" is delegated to the host via
    /// <see cref="OnSignIn"/>.
    /// </summary>
    public sealed partial class SettingsScreen : LvnOverlayScreen
    {
        /// <summary>Host hook for the "Sign in" button — route to the auth screen
        /// / platform sign-in. Null hides the button.</summary>
        public System.Func<Task> OnSignIn;

        // ── хранилище (кнопка «Скачать всю игру», ELVIN-85) ──────────────────
        // Хост (NovelApp) отдаёт оценку недокачанного, запуск батча, прогресс
        // и очистку — экран только рисует. Null-хуки прячут секцию целиком.
        public System.Func<Task<(long missingBytes, int missingCount, long usedBytes)>> StorageInfo;
        public System.Func<Task> DownloadAll;
        public System.Func<Task> ClearDownloads;
        public System.Func<(long received, long expected, bool active)> DownloadProgress;

        // Треки меню (ui.browse.music_options): хост отдаёт список и обработчик
        // смены — экран рисует пилюли. Пусто — строка не показывается.
        public System.Collections.Generic.List<(string id, string title)> MenuTracks;
        public System.Action<string> OnMenuTrack;

        private readonly SettingsConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly ScrollView _list;
        private readonly Color _text;
        private readonly Color _dim;
        private readonly Color _accent;
        private readonly float _radius;

        private VisualElement _accountRow;

        public SettingsScreen(SettingsConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new SettingsConfig();
            _assets = assets;
            _text = UiColor.Parse(_cfg.text_color, LvnTokens.Text);
            _dim = UiColor.Parse(_cfg.dim_text_color, LvnTokens.TextDim);
            _accent = UiColor.Parse(_cfg.accent_color, LvnTokens.Accent);
            _radius = _cfg.corner_radius ?? 12f;

            ScreenUi.Stretch(this);
            style.backgroundColor = UiColor.Parse(_cfg.scrim_color, LvnTokens.Scrim);
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            RegisterCallback<ClickEvent>(e => { if (e.target == this) Close(); });

            // Поля шире общих — настройки длинный список, ему нужен воздух по
            // краям. Цвет от новеллы передаём ЯВНО: до этого он ставился строкой
            // выше и тут же затирался обёрткой, то есть не работал вовсе.
            var sheet = Sheet(sideInset: 6f, topInset: 8f,
                              tint: string.IsNullOrEmpty(_cfg.panel_color)
                                        ? (Color?)null : UiColor.Parse(_cfg.panel_color, LvnTokens.PanelBg));
            sheet.style.paddingTop = 22; sheet.style.paddingBottom = 18;
            sheet.style.paddingLeft = 20; sheet.style.paddingRight = 20;

            var title = new Label(_cfg.title ?? LvnWords.Of("settings.title", "Settings"));
            LvnChrome.Heading(title);
            title.style.color = UiColor.Parse(_cfg.title_color, LvnTokens.Text);
            title.style.fontSize = 36;
            title.style.marginBottom = 14;
            sheet.Add(title);

            _list = new ScrollView(ScrollViewMode.Vertical);
            _list.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _list.style.flexGrow = 1;
            sheet.Add(_list);

            var close = new Button(Close) { text = _cfg.close_text ?? LvnWords.Of("common.close", "Close") };
            close.style.fontSize = 26;
            close.style.marginTop = 12;
            close.style.paddingTop = 12; close.style.paddingBottom = 12;
            close.style.color = _text;
            close.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.ClearBorder(close);
            LvnChrome.Round(close, _radius);
            sheet.Add(close);
        }


        /// <summary>(Re)build the settings rows from the current prefs/config. Called
        /// by <see cref="ShowAsync"/>; public so tests and hosts can render on demand.</summary>
        // БЕЗ ЭТОГО ЭКРАН ПУСТ: базовый ShowAsync зовёт OnOpening, а Rebuild
        // никто больше не вызывает — партнёр открыл «Настройки» из хаба и
        // увидел заголовок с кнопкой Close на пустом листе.
        protected override void OnOpening() => Rebuild();

        // ЛЕНТА, А НЕ ЧЕТЫРЕ ЭКРАНА. Вкладки прятали три четверти настроек за
        // переключателем: игрок, ищущий «размер текста», обязан был угадать, в
        // каком из четырёх разделов он лежит. Теперь всё лежит одной прокруткой
        // с заголовками, а пилюли сверху стали БЫСТРЫМ ПЕРЕХОДОМ — и заодно
        // показывают, в каком разделе игрок сейчас.
        private readonly Dictionary<string, VisualElement> _anchors = new Dictionary<string, VisualElement>();
        private readonly List<(string id, Button b)> _tabButtons = new List<(string, Button)>();
        private string _tab = "main";

        public void Rebuild()
        {
            _list.Clear();
            _anchors.Clear();
            _list.Add(TabsRow());

            Section("main", LvnWords.Of("settings.tab_main", "General"));
            _list.Add(SoundRow());
            if (_cfg.simple_audio ?? false)
            {
                // Два ползунка (решение партнёров): «Звуки» ведёт разом
                // эффекты, печать, интерфейс и эмбиент; голос — туда же.
                _list.Add(VolumeRow(LvnWords.Of("settings.music", "Music"), LvnWords.Of("settings.music_hint", "Story and menu tracks"),
                    () => LvnPrefs.VolMusic, v => LvnPrefs.VolMusic = v));
                _list.Add(VolumeRow(LvnWords.Of("settings.sounds", "Sounds"), LvnWords.Of("settings.sounds_hint", "Choices, scene effects and ambience"),
                    () => LvnPrefs.VolSfx,
                    v => { LvnPrefs.VolSfx = v; LvnPrefs.VolAmbient = v; LvnPrefs.VolVoice = v; }));
            }
            else
            {
                _list.Add(VolumeRow(LvnWords.Of("settings.music", "Music"), null, () => LvnPrefs.VolMusic, v => LvnPrefs.VolMusic = v));
                _list.Add(VolumeRow(LvnWords.Of("settings.ambient", "Ambience"), null, () => LvnPrefs.VolAmbient, v => LvnPrefs.VolAmbient = v));
                _list.Add(VolumeRow(LvnWords.Of("settings.sfx", "Effects"), null, () => LvnPrefs.VolSfx, v => LvnPrefs.VolSfx = v));
                _list.Add(VolumeRow(LvnWords.Of("settings.voice", "Voice"), null, () => LvnPrefs.VolVoice, v => LvnPrefs.VolVoice = v));
            }
            if (LvnPrefs.AvailableLocales != null && LvnPrefs.AvailableLocales.Count > 0)
                _list.Add(LanguageRow());
            if (MenuTracks != null && MenuTracks.Count > 1)
                _list.Add(MenuTrackRow());

            Section("reading", LvnWords.Of("settings.tab_reading", "Reading"));
            _list.Add(FontRow());
            _list.Add(TextScaleRow());
            _list.Add(UiScaleRow());
            _list.Add(RangeRow(LvnWords.Of("settings.text_speed", "Text speed"), LvnWords.Of("settings.text_speed_hint", "How fast lines type out"),
                0.25f, 3f, () => LvnPrefs.TextSpeed, v => LvnPrefs.TextSpeed = v));
            _list.Add(SwitchRow(LvnWords.Of("settings.auto_advance", "Auto-advance"), LvnWords.Of("settings.auto_advance_hint", "Lines turn by themselves"),
                () => LvnPrefs.AutoAdvance, v => LvnPrefs.AutoAdvance = v));
            _list.Add(RangeRow(LvnWords.Of("settings.auto_delay", "Auto delay"), LvnWords.Of("settings.auto_delay_hint", "Pause before the next line"),
                0.5f, 2.5f, () => LvnPrefs.AutoDelayScale, v => LvnPrefs.AutoDelayScale = v));
            _list.Add(RangeRow(LvnWords.Of("settings.box_opacity", "Box opacity"), LvnWords.Of("settings.box_opacity_hint", "The dialogue plate; text stays crisp"),
                0.2f, 1f, () => LvnPrefs.DialogOpacity, v => LvnPrefs.DialogOpacity = v));
            _list.Add(SwitchRow(LvnWords.Of("settings.skip_read", "Skip read only"), LvnWords.Of("settings.skip_read_hint", "Fast-forward stops at new lines"),
                () => LvnPrefs.SkipReadOnly, v => LvnPrefs.SkipReadOnly = v));
            _list.Add(SwitchRow(LvnWords.Of("settings.reduce_motion", "Reduce motion"), LvnWords.Of("settings.reduce_motion_hint", "No camera shake or flashes"),
                () => LvnPrefs.ReduceMotion, v => LvnPrefs.ReduceMotion = v));

            Section("data", LvnWords.Of("settings.tab_data", "Data"));
            _list.Add(ArtQualityRow());
            _list.Add(FpsRow());
            if (StorageInfo != null && DownloadAll != null)
                _list.Add(StorageRow());

            Section("account", LvnWords.Of("settings.tab_account", "Account"));
            _list.Add(UidRow());
            _accountRow = RowEx(_cfg.account_label ?? LvnWords.Of("settings.account", "Account"),
                LvnWords.Of("settings.account_hint", "Keeps progress and purchases on the server"));
            _list.Add(_accountRow);
            SetAccountStatus("…", showSignIn: false);
            _list.Add(RestoreRow());
            _list.Add(VersionRow());
            var links = LinksRow();
            if (links != null) _list.Add(links);
            var socials = SocialRow();
            if (socials != null) _list.Add(socials);

            // Подписка на прокрутку — ОДНА на экран. Вешать её в Rebuild
            // значило копить обработчики: после пятой пересборки каждое
            // движение ленты пересчитывало подсветку пять раз, и вкладки
            // «залипали» — обработчики спорили друг с другом.
            if (!_scrollHooked)
            {
                _scrollHooked = true;
                _list.contentContainer.RegisterCallback<GeometryChangedEvent>(_ => SyncActiveTab());
                _list.verticalScroller.valueChanged += _ => SyncActiveTab();
            }
        }

        private bool _scrollHooked;

        // Пока идёт переход по нажатию вкладки, подсветку ведёт НАЖАТИЕ, а не
        // прокрутка: лента едет мимо чужих разделов, и следящая подсветка
        // перебивала выбор игрока прямо под пальцем.
        private float _jumpUntil;

        // Заголовок раздела — он же якорь для быстрого перехода.
        private void Section(string id, string title)
        {
            var lbl = SectionTitle(title, LvnTokens.TextLg);
            lbl.style.marginTop = _anchors.Count == 0 ? 8 : 26;
            lbl.style.marginBottom = 8;
            _anchors[id] = lbl;
            _list.Add(lbl);
        }

        // Какой раздел игрок сейчас читает: верхний из тех, что уже проехали
        // верхнюю кромку. Подсветка следует за лентой, а не за последним
        // нажатием — иначе она врёт ровно в тот момент, когда на неё смотрят.
        private void SyncActiveTab()
        {
            if (_anchors.Count == 0 || _tabButtons.Count == 0) return;
            if (Lvn.UI.LvnClock.Now() < _jumpUntil) return;   // идёт переход по нажатию
            float top = _list.scrollOffset.y + 24f;
            string cur = null;
            foreach (var kv in _anchors)
            {
                var y = kv.Value.layout.y;
                if (float.IsNaN(y)) continue;
                if (y <= top || cur == null) cur = kv.Key;
            }
            if (cur == null || cur == _tab) return;
            _tab = cur;
            foreach (var (id, b) in _tabButtons) StyleValueButton(b, id == _tab);
        }

        // Пилюли-вкладки: быстрый переход к разделу, активная — акцентом.
        private VisualElement TabsRow()
        {
            _tabButtons.Clear();
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.marginBottom = 14;
            foreach (var (id, label) in new[]
            {
                ("main", LvnWords.Of("settings.tab_main", "General")), ("reading", LvnWords.Of("settings.tab_reading", "Reading")),
                ("data", LvnWords.Of("settings.tab_data", "Data")), ("account", LvnWords.Of("settings.tab_account", "Account")),
            })
            {
                var b = new Button { text = label };
                StyleValueButton(b, _tab == id);
                b.style.marginRight = 8;
                b.style.marginBottom = 8;
                b.style.flexGrow = 1;
                var captured = id;
                b.clicked += () =>
                {
                    if (!_anchors.TryGetValue(captured, out var anchor)) return;
                    _tab = captured;
                    foreach (var (tid, tb) in _tabButtons) StyleValueButton(tb, tid == _tab);
                    // Замок на время прокрутки: без него следящая подсветка
                    // возвращалась на прежний раздел, пока лента ещё едет.
                    _jumpUntil = Lvn.UI.LvnClock.Now() + 0.6f;
                    // Через кадр: до первой раскладки у якоря нет геометрии, и
                    // прокрутка «в никуда» выглядела как несработавшее нажатие.
                    _list.schedule.Execute(() => _list.ScrollTo(anchor)).ExecuteLater(16);
                };
                _tabButtons.Add((captured, b));
                row.Add(b);
            }
            return row;
        }

        // ── rows ──────────────────────────────────────────────────────────────


        // Трек главного меню — как в жанровых флагманах: пилюли с выбором.
        private VisualElement MenuTrackRow()
        {
            return WideRow(LvnWords.Of("settings.menu_track", "Menu track"),
                LvnWords.Of("settings.menu_track_hint", "What plays on the storefront"),
                Lvn.UI.LvnSegment.Of(MenuTracks,
                t => t.title,
                t => (LvnPrefs.MenuTrack ?? "") == t.id
                     || (string.IsNullOrEmpty(LvnPrefs.MenuTrack) && t.id == MenuTracks[0].id),
                t => { LvnPrefs.MenuTrack = t.id; OnMenuTrack?.Invoke(t.id); },
                StyleValueButton));
        }



        // ── шрифт и размеры ──────────────────────────────────────────────────
        // Просьба партнёра (TR-58): «в пункт „чтение“ просится выбор размера
        // шрифта». Сделано шире и по правилу «у понятия один дом»: гарнитура,
        // размер реплик и размер интерфейса — три разные величины, и путать их
        // нельзя. Читать длинный текст и попадать пальцем в кнопку — разные
        // задачи, и решаются они разными ручками.
        private VisualElement FontRow()
        {
            var options = new List<string> { "" };
            foreach (var f in Lvn.UI.LvnFonts.Families) options.Add(f.Id);
            return WideRow(LvnWords.Of("settings.font", "Font"),
                LvnWords.Of("settings.font_hint", "The typeface for lines and menus"),
                Lvn.UI.LvnSegment.Of(options,
                id => string.IsNullOrEmpty(id)
                    ? LvnWords.Of("settings.font_author", "As authored")
                    : Lvn.UI.LvnFonts.FamilyOf(id).Title,
                id => (LvnPrefs.FontFamily ?? "") == id,
                // Пересобирать экран не нужно: дом шрифтов переставит гарнитуру
                // всем, кому её ставил, — включая эти же кнопки.
                id => LvnPrefs.FontFamily = id,
                StyleValueButton));
        }

        // Ступени, а не ползунок: у размера текста нет «чуть-чуть» — есть
        // «читается» и «не читается», и пять названных ступеней игрок проходит
        // за пять нажатий вместо ловли доли на полоске.
        private static readonly (float k, string key, string en)[] ScaleSteps =
        {
            (0.85f, "settings.size_xs", "XS"), (0.92f, "settings.size_s", "S"),
            (1f, "settings.size_m", "M"), (1.15f, "settings.size_l", "L"),
            (1.3f, "settings.size_xl", "XL"),
        };

        private VisualElement TextScaleRow()
        {
            return WideRow(LvnWords.Of("settings.text_size", "Text size"),
                LvnWords.Of("settings.text_size_hint", "Dialogue lines only — the scene stays as authored"),
                Lvn.UI.LvnSegment.Of(ScaleSteps,
                st => LvnWords.Of(st.key, st.en),
                st => Mathf.Abs(LvnPrefs.TextScale - st.k) < 0.01f,
                st => LvnPrefs.TextScale = st.k,
                StyleValueButton));
        }

        private VisualElement UiScaleRow()
        {
            return WideRow(LvnWords.Of("settings.ui_size", "Interface size"),
                LvnWords.Of("settings.ui_size_hint", "Menus, buttons and panels — the scene keeps its framing"),
                Lvn.UI.LvnSegment.Of(ScaleSteps,
                st => LvnWords.Of(st.key, st.en),
                st => Mathf.Abs(LvnPrefs.UiScale - st.k) < 0.01f,
                st => { LvnPrefs.UiScale = st.k; Lvn.UI.LvnPanel.ApplyUiScale(); },
                StyleValueButton));
        }

        private VisualElement SoundRow()
        {
            var row = RowEx(_cfg.sound_label ?? LvnWords.Of("settings.sound", "All sounds"),
                LvnWords.Of("settings.mute_hint", "Turns music and effects fully off"));
            var btn = new Button { text = LvnPrefs.SoundOn ? (_cfg.on_text ?? LvnWords.Of("common.on", "On")) : (_cfg.off_text ?? LvnWords.Of("common.off", "Off")) };
            StyleValueButton(btn, LvnPrefs.SoundOn);
            btn.clicked += () =>
            {
                LvnPrefs.SoundOn = !LvnPrefs.SoundOn;
                btn.text = LvnPrefs.SoundOn ? (_cfg.on_text ?? LvnWords.Of("common.on", "On")) : (_cfg.off_text ?? LvnWords.Of("common.off", "Off"));
                StyleValueButton(btn, LvnPrefs.SoundOn);
            };
            row.Add(btn);
            return row;
        }

        // A per-channel volume slider (0–1) that reads the current pref and writes
        // it back live as the player drags. Sits under the master Sound toggle.
        // Слайдер произвольного диапазона — тем же видом, что громкости.
        private VisualElement RangeRow(string label, string hint, float min, float max,
            System.Func<float> get, System.Action<float> set)
        {
            var row = RowEx(label, hint);
            var slider = new Slider(min, max) { value = get() };
            slider.style.width = 200;
            slider.style.marginLeft = 12;
            var drag = slider.Q("unity-dragger");
            if (drag != null) drag.style.backgroundColor = _accent;
            slider.RegisterValueChangedCallback(evt => set(evt.newValue));
            row.Add(slider);
            return row;
        }

        // Булева строка пилюлями Вкл/Выкл — как «Все звуки».
        private VisualElement SwitchRow(string label, string hint,
            System.Func<bool> get, System.Action<bool> set)
        {
            var row = RowEx(label, hint);
            row.Add(Lvn.UI.LvnSegment.Of(new[] { true, false },
                v => v ? LvnWords.Of("common.on", "On") : LvnWords.Of("common.off", "Off"),
                v => get() == v,
                v => set(v),
                StyleValueButton));
            return row;
        }

        private VisualElement VolumeRow(string label, string hint, System.Func<float> get, System.Action<float> set)
        {
            var row = RowEx(label, hint);
            var slider = new Slider(0f, 1f) { value = get() };
            slider.style.width = 200;
            slider.style.marginLeft = 12;
            var drag = slider.Q("unity-dragger");
            if (drag != null) drag.style.backgroundColor = _accent;
            slider.RegisterValueChangedCallback(evt => set(evt.newValue));
            row.Add(slider);
            return row;
        }


        private VisualElement LanguageRow()
        {
            var label = _cfg.language_label ?? LvnWords.Of("settings.language", "Story language");
            // The script's inline language, then each localized catalog.
            var options = new List<string> { "" };
            options.AddRange(LvnPrefs.AvailableLocales);
            // Через дом рядов: сколько у новеллы каталогов, столько и кнопок —
            // без переноса четвёртый язык уехал бы за край, как ступень арта.
            return WideRow(label, LvnWords.Of("settings.language_hint", "Chapter text; the interface follows it"),
                Lvn.UI.LvnSegment.Of(options,
                    LocaleName,
                    loc => LvnPrefs.Locale == loc,
                    loc => LvnPrefs.Locale = loc,   // NovelApp перечитает каталог сам
                    StyleValueButton));
        }






        // ── account status (async from /v1/auth/me) ─────────────────────────────



        // ── shared bits ─────────────────────────────────────────────────────────

        // A label + a right-aligned value area.
        // Заголовок смысловой группы: настройки читаются секциями, а не
        // простынёй строк (живой репорт «непонятный экран»).
        private VisualElement SectionTitle(string text)
        {
            var lbl = new Label(text.ToUpperInvariant());
            lbl.style.color = _dim;
            lbl.style.fontSize = 19;
            lbl.style.letterSpacing = 2.5f;
            lbl.style.marginTop = 18;
            lbl.style.marginBottom = 4;
            return lbl;
        }

        // Строка «название + пояснение» — у каждой настройки есть подпись,
        // объясняющая, что она делает: контрол без пояснения выглядит дико
        // («кнопка скачать игру?????» — живой репорт).
        /// <summary>
        /// ШИРОКИЙ РЯД — подпись сверху, управление во всю ширину под ней.
        ///
        /// <para>Обычная строка делит ширину между текстом и управлением, и на
        /// телефоне вариантам достаётся треть: три ступени качества арта
        /// переносились в столбик по краю, а «1K» на снимке партнёра выглядел
        /// оторванным от своего ряда. Там, где вариантов больше двух, подпись и
        /// выбор не спорят за место — они стоят друг под другом.</para>
        /// </summary>
        private VisualElement WideRow(string label, string hint, VisualElement control)
        {
            var row = new VisualElement();
            row.style.marginBottom = 8;
            row.style.paddingTop = 12;
            row.style.paddingBottom = 12;
            var lbl = new Label(label);
            lbl.style.color = _text;
            lbl.style.fontSize = LvnTokens.TextBase;
            row.Add(lbl);
            if (!string.IsNullOrEmpty(hint))
            {
                var h = new Label(hint);
                h.style.color = _dim;
                h.style.fontSize = LvnTokens.TextSm;
                h.style.marginTop = 8;
                h.style.whiteSpace = WhiteSpace.Normal;
                row.Add(h);
            }
            if (control != null)
            {
                control.style.marginTop = 12;
                row.Add(control);
            }
            return row;
        }

        private VisualElement RowEx(string label, string hint)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginBottom = 6;
            row.style.paddingTop = 10; row.style.paddingBottom = 10;
            var col = new VisualElement();
            col.style.flexGrow = 1;
            col.style.flexShrink = 1;
            col.style.marginRight = 12;
            var lbl = new Label(label);
            lbl.style.color = _text;
            lbl.style.fontSize = 26;
            col.Add(lbl);
            if (!string.IsNullOrEmpty(hint))
            {
                var h = new Label(hint);
                h.style.color = _dim;
                h.style.fontSize = 19;
                h.style.marginTop = 2;
                h.style.whiteSpace = WhiteSpace.Normal;
                col.Add(h);
            }
            row.Add(col);
            return row;
        }

        private VisualElement Row(string label)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginBottom = 6;
            row.style.paddingTop = 10; row.style.paddingBottom = 10;
            var lbl = new Label(label);
            lbl.style.color = _text;
            lbl.style.fontSize = 26;
            lbl.style.flexGrow = 1;
            row.Add(lbl);
            return row;
        }

        private Label LinkLabel(string text, string url)
        {
            var lbl = new Label(text);
            lbl.style.color = _accent;
            lbl.style.fontSize = 22;
            lbl.RegisterCallback<ClickEvent>(_ => LvnWebView.Open(url));
            return lbl;
        }

        private void StyleValueButton(Button b, bool active)
        {
            b.style.fontSize = 24;
            b.style.paddingTop = 8; b.style.paddingBottom = 8;
            b.style.paddingLeft = 16; b.style.paddingRight = 16;
            // Роль — «один из вариантов», но палитру приносит новелла
            // (accent_color/text_color в манифесте), поэтому не Choice, а Plate.
            LvnStyler.Plate(b,
                active ? _accent : LvnTokens.Faint,
                active ? LvnTokens.OnAccent : _text, _radius);
        }

        // Пилюля оригинала носит ИМЯ ЯЗЫКА («Русский»), а не слово «Оригинал»
        // — «Оригинал/Русский» при русском оригинале читалось бессмыслицей.
        private static string LocaleName(string loc) => LvnPrefs.LocaleTitle(loc);

        private static string Capitalize(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
