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
    public sealed class SettingsScreen : LvnOverlayScreen
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

            var title = new Label(_cfg.title ?? "Settings");
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

            var close = new Button(Close) { text = _cfg.close_text ?? "Close" };
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

        public void Rebuild()
        {
            _list.Clear();
            // ЕДИНЫЕ настройки (решение Ильи 26.08): глобальные и внутриигровые
            // в одной панели с секциями; квик-меню сцены открывает ЕЁ ЖЕ.
            _list.Add(SectionTitle("Основные"));
            _list.Add(SoundRow());
            if (_cfg.simple_audio ?? false)
            {
                // Два ползунка (решение партнёров): «Звуки» ведёт разом эффекты,
                // печать, интерфейс и эмбиент; голос — туда же.
                _list.Add(VolumeRow("Музыка", "Треки историй и главного меню",
                    () => LvnPrefs.VolMusic, v => LvnPrefs.VolMusic = v));
                _list.Add(VolumeRow("Звуки", "Выборы, эффекты сцен и атмосфера",
                    () => LvnPrefs.VolSfx,
                    v => { LvnPrefs.VolSfx = v; LvnPrefs.VolAmbient = v; LvnPrefs.VolVoice = v; }));
            }
            else
            {
                _list.Add(VolumeRow("Музыка", null, () => LvnPrefs.VolMusic, v => LvnPrefs.VolMusic = v));
                _list.Add(VolumeRow("Эмбиент", null, () => LvnPrefs.VolAmbient, v => LvnPrefs.VolAmbient = v));
                _list.Add(VolumeRow("Эффекты", null, () => LvnPrefs.VolSfx, v => LvnPrefs.VolSfx = v));
                _list.Add(VolumeRow("Голос", null, () => LvnPrefs.VolVoice, v => LvnPrefs.VolVoice = v));
            }
            if (LvnPrefs.AvailableLocales != null && LvnPrefs.AvailableLocales.Count > 0)
                _list.Add(LanguageRow());
            if (MenuTracks != null && MenuTracks.Count > 1)
                _list.Add(MenuTrackRow());

            // Настройки чтения — жили только во внутриигровой панели, теперь
            // здесь: одна правда для игрока в любом контексте.
            _list.Add(SectionTitle("Чтение"));
            _list.Add(RangeRow("Скорость текста", "Темп печати реплик",
                0.25f, 3f, () => LvnPrefs.TextSpeed, v => LvnPrefs.TextSpeed = v));
            _list.Add(SwitchRow("Авто-чтение", "Реплики листаются сами",
                () => LvnPrefs.AutoAdvance, v => LvnPrefs.AutoAdvance = v));
            _list.Add(RangeRow("Задержка авто", "Пауза перед следующей репликой",
                0.5f, 2.5f, () => LvnPrefs.AutoDelayScale, v => LvnPrefs.AutoDelayScale = v));
            _list.Add(RangeRow("Прозрачность окна", "Плашка диалога; текст всегда чёткий",
                0.2f, 1f, () => LvnPrefs.DialogOpacity, v => LvnPrefs.DialogOpacity = v));
            _list.Add(SwitchRow("Скип: только прочитанное", "Перемотка стоит на новых репликах",
                () => LvnPrefs.SkipReadOnly, v => LvnPrefs.SkipReadOnly = v));
            _list.Add(SwitchRow("Меньше движения", "Без тряски камеры и вспышек",
                () => LvnPrefs.ReduceMotion, v => LvnPrefs.ReduceMotion = v));

            if (StorageInfo != null && DownloadAll != null)
            {
                _list.Add(SectionTitle("Данные"));
                _list.Add(ArtQualityRow());
                _list.Add(FpsRow());
                _list.Add(StorageRow());
            }
            _list.Add(SectionTitle("Аккаунт"));
            _list.Add(UidRow());
            _accountRow = RowEx(_cfg.account_label ?? "Аккаунт",
                "Хранит прогресс и покупки на сервере");
            _list.Add(_accountRow);
            SetAccountStatus("…", showSignIn: false);
            _list.Add(RestoreRow());
            _list.Add(SectionTitle("О приложении"));
            _list.Add(VersionRow());
            var links = LinksRow();
            if (links != null) _list.Add(links);
            var socials = SocialRow();
            if (socials != null) _list.Add(socials);
        }

        // ── rows ──────────────────────────────────────────────────────────────

        // «Скачать всю игру»: строка-автомат — оценка → загрузка с живыми
        // мегабайтами → «Скачано» с кнопкой удаления. Играть можно и без неё
        // (стриминг), кнопка — для самолёта и плохой сети.
        private VisualElement StorageRow()
        {
            var row = RowEx("Игра целиком",
                "Скачайте истории заранее, чтобы играть без интернета. " +
                "Пока не скачано — главы загружаются по мере чтения.");
            var status = new Label("…");
            status.style.color = _dim;
            status.style.fontSize = 13;
            status.style.marginRight = 8;
            row.Add(status);
            var btn = new Button { text = "…" };
            StyleValueButton(btn, true);
            btn.SetEnabled(false);
            row.Add(btn);

            bool downloaded = false;
            IVisualElementScheduledItem ticker = null;

            async Task RefreshAsync()
            {
                ticker?.Pause();
                var (missing, count, used) = await StorageInfo();
                downloaded = count == 0;
                if (downloaded)
                {
                    status.text = $"скачано · занято {used >> 20} МБ";
                    btn.text = "Удалить";
                    btn.SetEnabled(ClearDownloads != null);
                }
                else
                {
                    status.text = "";
                    btn.text = $"Скачать ≈{System.Math.Max(1, missing >> 20)} МБ";
                    btn.SetEnabled(true);
                }
            }

            btn.clicked += () =>
            {
                if (!downloaded)
                {
                    btn.SetEnabled(false);
                    _ = DownloadAll();
                    // Живой прогресс в мегабайтах, пока батч активен.
                    ticker = row.schedule.Execute(() =>
                    {
                        var p = DownloadProgress?.Invoke() ?? (0, 0, false);
                        if (p.active)
                            status.text = $"загрузка… {p.received >> 20} / {System.Math.Max(p.expected, p.received) >> 20} МБ";
                        else
                            _ = RefreshAsync();
                    }).Every(500);
                }
                else
                {
                    btn.SetEnabled(false);
                    _ = Run();
                    async Task Run() { await ClearDownloads(); await RefreshAsync(); }
                }
            };

            _ = RefreshAsync();
            return row;
        }

        // Трек главного меню — как в жанровых флагманах: пилюли с выбором.
        private VisualElement MenuTrackRow()
        {
            var row = RowEx("Трек меню", "Какая музыка играет на витрине");
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

        // Качество арта: авто-режим движка против ручного пресета конкурентов —
        // но ручка экономии полезна на дорогом трафике.
        private VisualElement ArtQualityRow()
        {
            bool auto = string.IsNullOrEmpty(LvnPrefs.ArtQuality);
            var row = RowEx("Качество арта",
                (auto ? "Подобрано под ваш экран автоматически. " : "")
                + "Ниже ступень — меньше трафика и памяти. Скачанное "
                + "перекачается в новом качестве само");
            var seg = new VisualElement();
            seg.style.flexDirection = FlexDirection.Row;
            row.Add(seg);
            var buttons = new List<(string q, Button b)>();
            string Current() => string.IsNullOrEmpty(LvnPrefs.ArtQuality)
                ? Lvn.UI.Screens.NovelApp.EffectiveArtQuality()
                : LvnPrefs.ArtQuality;
            void Highlight()
            {
                foreach (var (q, b) in buttons) StyleValueButton(b, Current() == q);
            }
            foreach (var (q, label) in new[] { ("2k", "2K"), ("1440", "1440p"), ("1k", "1K") })
            {
                var btn = new Button { text = label };
                btn.style.marginLeft = 6;
                var quality = q;
                btn.clicked += () => { LvnPrefs.ArtQuality = quality; Highlight(); };
                buttons.Add((q, btn));
                seg.Add(btn);
            }
            Highlight();
            return row;
        }

        private VisualElement FpsRow()
        {
            var row = RowEx("Кадровая частота",
                "30 кадров — дольше живёт батарея; 60 — плавнее анимации");
            var seg = new VisualElement();
            seg.style.flexDirection = FlexDirection.Row;
            row.Add(seg);
            Button f30 = null, f60 = null;
            void Highlight()
            {
                StyleValueButton(f30, LvnPrefs.TargetFps == 30);
                StyleValueButton(f60, LvnPrefs.TargetFps != 30);
            }
            f30 = new Button { text = "30" };
            f30.style.marginLeft = 6;
            f30.clicked += () => { LvnPrefs.TargetFps = 30; Highlight(); };
            f60 = new Button { text = "60" };
            f60.style.marginLeft = 6;
            f60.clicked += () => { LvnPrefs.TargetFps = 60; Highlight(); };
            seg.Add(f30); seg.Add(f60);
            Highlight();
            return row;
        }

        private VisualElement SoundRow()
        {
            var row = RowEx(_cfg.sound_label ?? "Все звуки",
                "Полностью выключает музыку и эффекты");
            var btn = new Button { text = LvnPrefs.SoundOn ? (_cfg.on_text ?? "Вкл") : (_cfg.off_text ?? "Выкл") };
            StyleValueButton(btn, LvnPrefs.SoundOn);
            btn.clicked += () =>
            {
                LvnPrefs.SoundOn = !LvnPrefs.SoundOn;
                btn.text = LvnPrefs.SoundOn ? (_cfg.on_text ?? "Вкл") : (_cfg.off_text ?? "Выкл");
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
            on = new Button { text = "Вкл" };
            on.style.marginLeft = 6;
            on.clicked += () => { set(true); Highlight(); };
            off = new Button { text = "Выкл" };
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

        // "Restore purchases": re-syncs the wallet from the server, which re-grants
        // any purchases the account already owns. (Real platform restore is host-side.)
        private VisualElement RestoreRow()
        {
            var row = RowEx("Восстановить покупки",
                "Если после переустановки пропали покупки — нажмите");
            var btn = new Button { text = "Восстановить" };
            StyleValueButton(btn, false);
            btn.clicked += () =>
            {
                LvnAsync.Fire(Lvn.Services.LvnWallet.RefreshAsync(), "Refresh");
                btn.text = "…";
                btn.schedule.Execute(() => btn.text = "Готово").ExecuteLater(1200);
            };
            row.Add(btn);
            return row;
        }

        private VisualElement LanguageRow()
        {
            var row = RowEx(_cfg.language_label ?? "Язык истории",
                "Текст глав; интерфейс следует за ним");
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

        private VisualElement UidRow()
        {
            var row = RowEx(_cfg.uid_label ?? "ID игрока",
                "Назовите его, если обратитесь в поддержку");
            var uid = LvnBackend.UserId;
            var shortId = string.IsNullOrEmpty(uid) ? "—" : (uid.Length > 12 ? uid.Substring(0, 12) + "…" : uid);
            var val = new Label(shortId);
            val.style.color = _dim;
            val.style.fontSize = 22;
            val.style.marginRight = 10;
            row.Add(val);

            var copy = new Button { text = _cfg.copy_text ?? "Копировать" };
            StyleValueButton(copy, false);
            copy.SetEnabled(!string.IsNullOrEmpty(uid));
            copy.clicked += () =>
            {
                GUIUtility.systemCopyBuffer = uid ?? "";
                copy.text = _cfg.copied_text ?? "Скопировано";
                copy.schedule.Execute(() => copy.text = _cfg.copy_text ?? "Копировать").ExecuteLater(1200);
            };
            row.Add(copy);
            return row;
        }

        private VisualElement VersionRow()
        {
            var row = RowEx(_cfg.version_label ?? "Версия", null);
            var val = new Label(Application.version + EditorBuildStamp());
            val.style.color = _dim;
            val.style.fontSize = 22;
            row.Add(val);
            return row;
        }

        /// <summary>В РЕДАКТОРЕ — время сборки движка рядом с версией.
        /// Unity не пересобирает C# на ходу: правка, сделанная во время Play,
        /// доедет только после Stop→Play, и снаружи это неотличимо от «фича не
        /// работает». Штамп отвечает на вопрос «я вообще на свежем коде?» за
        /// пять секунд, без консоли. В собранной игре строки нет.</summary>
        private static string EditorBuildStamp()
        {
#if UNITY_EDITOR
            try
            {
                var path = typeof(Lvn.UI.VnStage).Assembly.Location;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return "";
                return "  · движок " + System.IO.File.GetLastWriteTime(path).ToString("HH:mm:ss");
            }
            catch { return ""; }
#else
            return "";
#endif
        }

        private VisualElement LinksRow()
        {
            bool hasTerms = !string.IsNullOrEmpty(_cfg.terms_url);
            bool hasPrivacy = !string.IsNullOrEmpty(_cfg.privacy_url);
            if (!hasTerms && !hasPrivacy) return null;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.Center;
            row.style.marginTop = 8; row.style.marginBottom = 6;
            if (hasTerms) row.Add(LinkLabel(_cfg.terms_text ?? "Terms of Use", _cfg.terms_url));
            if (hasTerms && hasPrivacy)
            {
                var dot = new Label("·"); dot.style.color = _dim; dot.style.marginLeft = 10; dot.style.marginRight = 10;
                row.Add(dot);
            }
            if (hasPrivacy) row.Add(LinkLabel(_cfg.privacy_text ?? "Privacy Policy", _cfg.privacy_url));
            return row;
        }

        private VisualElement SocialRow()
        {
            if (_cfg.social == null || _cfg.social.Count == 0) return null;
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.Center;
            row.style.marginTop = 12;
            foreach (var s in _cfg.social)
            {
                if (s == null || string.IsNullOrEmpty(s.url)) continue;
                VisualElement el;
                if (!string.IsNullOrEmpty(s.icon))
                {
                    var icon = new VisualElement();
                    icon.style.width = 44; icon.style.height = 44;
                    icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                    icon.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                    LvnAsync.Fire(ScreenUi.AssignBgAsync(icon, s.icon, _assets), "AssignBg");
                    el = icon;
                }
                else
                {
                    var lbl = new Label(s.name ?? "link");
                    lbl.style.color = _accent;
                    lbl.style.fontSize = 24;
                    el = lbl;
                }
                el.style.marginLeft = 10; el.style.marginRight = 10;
                var url = s.url;
                el.RegisterCallback<ClickEvent>(_ => LvnWebView.Open(url));
                row.Add(el);
            }
            return row;
        }

        // ── account status (async from /v1/auth/me) ─────────────────────────────

        private async Task RefreshAccountAsync()
        {
            var providers = await LvnBackend.GetProvidersAsync();
            if (!IsOpen || _accountRow == null) return;
            if (providers != null && providers.Length > 0)
            {
                string via = string.Join(", ", System.Array.ConvertAll(providers, Capitalize));
                SetAccountStatus((_cfg.signed_in_text ?? "Signed in") + " · " + via, showSignIn: false);
            }
            else
            {
                // A device-only (or offline) account — offer to link Google/Apple.
                string via = _cfg.device_text ?? "device";
                SetAccountStatus((_cfg.signed_in_text ?? "Signed in") + " · " + via, showSignIn: OnSignIn != null);
            }
        }

        private void SetAccountStatus(string text, bool showSignIn)
        {
            if (_accountRow == null) return;
            // Rebuild the row's value side (keep the label at index 0).
            for (int i = _accountRow.childCount - 1; i >= 1; i--)
                _accountRow.RemoveAt(i);
            var val = new Label(text);
            val.style.color = _dim;
            val.style.fontSize = 22;
            val.style.marginRight = 10;
            _accountRow.Add(val);
            if (showSignIn)
            {
                var btn = new Button { text = _cfg.sign_in_text ?? "Sign in" };
                StyleValueButton(btn, true);
                btn.clicked += () => { if (OnSignIn != null) _ = OnSignIn(); };
                _accountRow.Add(btn);
            }
        }

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
            b.style.color = active ? LvnTokens.OnAccent : _text;
            b.style.backgroundColor = active ? _accent : LvnTokens.Faint;
            LvnChrome.ClearBorder(b);
            LvnChrome.Round(b, _radius);
        }

        // Пилюля оригинала носит ИМЯ ЯЗЫКА («Русский»), а не слово «Оригинал»
        // — «Оригинал/Русский» при русском оригинале читалось бессмыслицей.
        private static string LocaleName(string loc) => LvnPrefs.LocaleTitle(loc);

        private static string Capitalize(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
