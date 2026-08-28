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

            var sheet = new VisualElement();
            sheet.style.position = Position.Absolute;
            sheet.style.left = Length.Percent(6f);
            sheet.style.right = Length.Percent(6f);
            sheet.style.top = Length.Percent(8f);
            sheet.style.bottom = Length.Percent(8f);
            sheet.style.backgroundColor = UiColor.Parse(_cfg.panel_color, LvnTokens.PanelBg);
            LvnChrome.Round(sheet, _radius + 4f);
            LvnChrome.Edge(sheet);
            sheet.style.paddingTop = 22; sheet.style.paddingBottom = 18;
            sheet.style.paddingLeft = 20; sheet.style.paddingRight = 20;
            Add(sheet);
            AdoptSheet(sheet); // единый враппер попапа: стекло, окантовка, подъезд

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

        // Активная вкладка настроек — простыня из ~18 строк группируется
        // (решение Ильи 26.08: «люди будут теряться»): виден один короткий
        // экран, переключение пилюлями сверху.
        private string _tab = "main";

        public void Rebuild()
        {
            _list.Clear();
            _list.Add(TabsRow());

            switch (_tab)
            {
                case "main":
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
                    break;

                case "reading":
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
                    break;

                case "data":
                    _list.Add(ArtQualityRow());
                    _list.Add(FpsRow());
                    if (StorageInfo != null && DownloadAll != null)
                        _list.Add(StorageRow());
                    break;

                case "account":
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
                    break;
            }
        }

        // Пилюли-вкладки: активная — акцентом.
        private VisualElement TabsRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
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
                b.style.flexGrow = 1;
                var captured = id;
                b.clicked += () => { _tab = captured; Rebuild(); };
                row.Add(b);
            }
            return row;
        }

        // ── rows ──────────────────────────────────────────────────────────────


        // Трек главного меню — как в жанровых флагманах: пилюли с выбором.
        private VisualElement MenuTrackRow()
        {
            var row = RowEx(LvnWords.Of("settings.menu_track", "Menu track"), LvnWords.Of("settings.menu_track_hint", "What plays on the storefront"));
            var seg = new VisualElement();
            seg.style.flexDirection = FlexDirection.Row;
            seg.style.flexWrap = Wrap.Wrap;
            row.Add(seg);
            var buttons = new System.Collections.Generic.List<(Button b, string id)>();
            void Highlight()
            {
                foreach (var (b, id) in buttons)
                    StyleValueButton(b, (LvnPrefs.MenuTrack ?? "") == id
                        || (string.IsNullOrEmpty(LvnPrefs.MenuTrack) && id == MenuTracks[0].id));
            }
            foreach (var (id, title) in MenuTracks)
            {
                var captured = id;
                var b = new Button { text = title };
                b.style.marginLeft = 6; b.style.marginBottom = 6;
                b.clicked += () =>
                {
                    LvnPrefs.MenuTrack = captured;
                    OnMenuTrack?.Invoke(captured);
                    Highlight();
                };
                buttons.Add((b, captured));
                seg.Add(b);
            }
            Highlight();
            return row;
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
            var seg = new VisualElement();
            seg.style.flexDirection = FlexDirection.Row;
            row.Add(seg);
            Button on = null, off = null;
            void Highlight() { StyleValueButton(on, get()); StyleValueButton(off, !get()); }
            on = new Button { text = LvnWords.Of("common.on", "On") };
            on.style.marginLeft = 6;
            on.clicked += () => { set(true); Highlight(); };
            off = new Button { text = LvnWords.Of("common.off", "Off") };
            off.style.marginLeft = 6;
            off.clicked += () => { set(false); Highlight(); };
            seg.Add(on); seg.Add(off);
            Highlight();
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
            var row = RowEx(_cfg.language_label ?? LvnWords.Of("settings.language", "Story language"),
                LvnWords.Of("settings.language_hint", "Chapter text; the interface follows it"));
            var seg = new VisualElement();
            seg.style.flexDirection = FlexDirection.Row;
            row.Add(seg);

            // The script's inline language, then each localized catalog.
            var options = new List<string> { "" };
            options.AddRange(LvnPrefs.AvailableLocales);
            var buttons = new List<(Button b, string loc)>();
            void Highlight() { foreach (var (b, loc) in buttons) StyleValueButton(b, LvnPrefs.Locale == loc); }
            foreach (var loc in options)
            {
                var captured = loc;
                var b = new Button { text = LocaleName(loc) };
                b.style.marginLeft = 6;
                b.clicked += () =>
                {
                    LvnPrefs.Locale = captured; // NovelApp reloads the string catalog live
                    Highlight();
                };
                buttons.Add((b, captured));
                seg.Add(b);
            }
            Highlight();
            return row;
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
