using System;
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
    /// The IN-STORY wardrobe: a bottom sheet over the running scene — the
    /// genre-standard "dress up mid-chapter" moment. No preview pane: the LIVE
    /// actor on stage is the mirror. Browsing writes try-on values into
    /// <see cref="LvnWardrobe.Preview"/>, which the stage picks up instantly;
    /// the confirm button buys whatever's previewed-but-unowned (wallet skus),
    /// commits every previewed slot and closes; the collapse arrow cancels and
    /// the actor snaps back. Layout mirrors the reference flow: icon tabs per
    /// slot, a ◀ item name ▶ carousel, one big confirm with the total price.
    /// Opened by <c>ext wardrobe_show char=id</c> (the story holds meanwhile).
    /// </summary>
    public sealed partial class WardrobeSheet : VisualElement, Lvn.UI.ILvnRedress, ILvnHides, ILvnContentAware
    {
        private readonly WardrobeConfig _cfg;
        private readonly DialogueConfig _dlg;
        private readonly ChoicesConfig _ch;
        private readonly ILvnAssets _assets;
        private readonly Color _text, _dim, _accent, _accentText;
        private readonly float _radius;

        private readonly Label _title;
        private readonly VisualElement _tabs;
        private readonly Label _itemName;
        private Button _prevBtn, _nextBtn;
        private VisualElement _emoBar, _emoThumb;
        private const float EmoBarWidth = 6f;    // сама дорожка
        private const float EmoBarLane = 16f;    // полоса, которую колонка ей уступает
        private const int EmoBarSegments = 4;
        private readonly Button _confirm;
        private readonly Button _cancel;
        private Label _peekLabel;

        /// <summary>
        /// Слова или шрифт сменились. Гардероб держит состояние примерки —
        /// пересобирать его целиком нельзя: игрок потеряет выбранную вещь и
        /// открытую вкладку. Поэтому подписи обновляются на месте, а плитки
        /// перечитывают названия своей же лентой.
        /// </summary>
        public void Redress()
        {
            if (_title != null) _title.text = LvnWords.Pick("wardrobe.title", _cfg.title, "Wardrobe");
            if (_peekLabel != null) _peekLabel.text = LvnWords.Pick("wardrobe.peek", _cfg.peek_text, "Full height");
            RefreshConfirm();     // «Выбрать» / «Купить за N» — своя логика подписи
            RebuildStrip();       // плитки несут названия нарядов
            RebuildSubRow(false); // подписи подосей («Основа», «Цвет волос»)
            RefreshLabel();       // строка выбора под лентой
        }

        /// <summary>Просьба к хозяину панели убрать интерфейс с глаз (и вернуть
        /// его). Хост знает, где живёт панель; лист — нет.</summary>
        public Action<bool> OnPeek;
        private readonly VisualElement _balances;

        /// <summary>Host hook: open the currency store (the pills' "+" tap).
        /// NovelShell wires this to its StoreScreen.</summary>
        public System.Func<Task> OpenStore;
        /// <summary>Host hook: "not enough X — go to the store?" (title, message) →
        /// true to open the store and retry the purchase once. Same "ONE store
        /// everywhere" pattern as the chapter/title entry gates
        /// (NovelApp.ChargeChapterEntryAsync) — NovelShell wires this to its
        /// universal ConfirmAsync popup.</summary>
        public System.Func<string, string, Task<bool>> ConfirmTopUp;
        /// <summary>Host hook: a final "still not enough" notice after the
        /// store-and-retry above still failed.</summary>
        public System.Func<string, string, Task> Alert;
        /// <summary>Fired on confirm, once per equipped axis that carries a story `var`
        /// (entity, storyVar, value) — the host writes it back into the novel's state.
        /// Not fired on cancel/collapse or for a skipped axis, so an unchanged slot
        /// keeps its current value.</summary>
        public System.Action<string, string, string> OnEquip;

        /// <summary>Fired when the player switches the character pill
        /// (fromEntity, toEntity) — the host swaps who stands on the canvas.
        /// The sheet clears the outgoing character's previews itself.</summary>
        public System.Action<string, string> OnCharacterPicked;

        /// <summary>The character currently being dressed (BuildFor target) —
        /// hosts read it on close to strike the right actor from the stage.</summary>
        public string CurrentEntity => _entity;

        /// <summary>The always-open (quick-menu) mode: list ONLY outfits that
        /// crossed the player's path — worn by an actor, offered by a story
        /// wardrobe moment, or already owned. A story-opened sheet (false)
        /// shows the author's full catalog for the beat and MARKS it seen.
        /// Set before every ShowAsync — the instance is shared between paths.</summary>
        public bool OnlySeen;

        /// <summary>Какую зону куклы смотрит игрок: ось активного раздела
        /// (null — общий план: таб «Все», «Во весь рост», закрытие). Хост со
        /// сценой (NovelApp) наводит по нему камеру — тот же приём, что зум к
        /// лицам фаворитов в прологе (просьба Ильи 28.08).</summary>
        public static event Action<string> SectionFocus;

        private void FireSectionFocus(string axis) => SectionFocus?.Invoke(axis);
        /// <summary>Вернуть зум активного раздела (возврат из «Во весь рост»).</summary>
        public void RefocusSection() => FireSectionFocus(_tab);

        private LvnManifest _manifest;
        private string _entity;
        private LvnSpriteEntity _def;
        private string _tab;
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(); // axis → carousel pos
        private ScrollView _strip;                 // лента карточек активной оси
        private VisualElement _subRow;             // свотчи осей-поднастроек (subOf)
        private readonly List<VisualElement> _stripCards = new List<VisualElement>();
        private ScrollView _emotions;              // баблики эмоций (ось emotion)
        private string _emotionAxis;               // имя оси лиц текущего персонажа

        // Английские умолчания палитры articy; подпись берёт словарь по ключу
        // emotion.<имя>, поэтому новелла зовёт эмоции своими словами, а
        // незнакомую движок показывает как есть.
        private static readonly Dictionary<string, string> EmotionEn = new Dictionary<string, string>
        {
            ["idle"] = "Calm", ["medium"] = "Neutral", ["happy"] = "Happy",
            ["sad"] = "Sad", ["anger"] = "Anger", ["flirt"] = "Flirt",
            ["delight"] = "Delight", ["surprised"] = "Surprised", ["fear"] = "Fear",
            ["boredom"] = "Bored", ["discontent"] = "Discontent", ["dreamy"] = "Dreamy",
            ["horny"] = "Passion", ["offence"] = "Offence", ["sarcasm"] = "Sarcasm",
            ["shame"] = "Shy", ["sleep"] = "Asleep", ["smirk"] = "Smirk",
            ["tears"] = "Tears", ["thoughtfulness"] = "Thoughtful",
        };

        /// <summary>Подпись эмоции: слово новеллы → английское умолчание → сам код.</summary>
        internal static string EmotionLabel(string key)
            => LvnWords.Of("emotion." + key, EmotionEn.TryGetValue(key, out var en) ? en : key);

        /// <summary>Меню-режим: пилюли кошелька прячутся — валюты уже несёт
        /// единый навбар, дубль над плашкой только шумит.</summary>
        public bool HideBalances;

        /// <summary>Сюжетный показ (true, по умолчанию) метит весь предложенный
        /// каталог «встреченным» — он вошёл в путь игрока. Меню-магазин скинов
        /// ставит false: там ВЕСЬ каталог виден как витрина, но коллекцию
        /// игрового гардероба листание витрины раскрывать не должно.</summary>
        public bool MarkSeenOnShow = true;

        private readonly LvnCloseGate _gate = new LvnCloseGate();
        private bool _open;
        private bool _buying;

        public WardrobeSheet(WardrobeConfig cfg, ILvnAssets assets)
            : this(cfg, null, null, assets) { }

        /// <summary>The NATIVE skin: the sheet dresses itself in the game's own
        /// dialogue form (panel art/colours from <paramref name="dlg"/>) with
        /// choice-styled buttons (<paramref name="ch"/>) — a themed title (the
        /// gothic frame, the parchment box…) skins the wardrobe for free, like
        /// every other piece of chrome. ui.wardrobe fields stay as overrides.
        /// The sheet is CONTENT for the stage's shared window
        /// (<c>VnPanelHost</c>) — the frame, position and show/hide transitions
        /// belong to the host, so the sheet draws no panel of its own and its
        /// ShowAsync only runs the logic.</summary>
        public WardrobeSheet(WardrobeConfig cfg, DialogueConfig dlg, ChoicesConfig ch, ILvnAssets assets)
        {
            _cfg = cfg ?? new WardrobeConfig();
            _dlg = dlg;
            _ch = ch;
            _assets = assets;
            _text = UiColor.Named(_cfg.text_color ?? _dlg?.text_color, new Color(0.95f, 0.93f, 0.88f));
            _dim = UiColor.Named(_cfg.dim_text_color, LvnTokens.TextDim);
            _accent = UiColor.Named(_cfg.accent_color ?? _dlg?.speaker_color, LvnTokens.Accent);
            _accentText = UiColor.Named(_cfg.accent_text_color, LvnTokens.OnAccent);
            _radius = _cfg.corner_radius ?? _dlg?.corner_radius ?? LvnTokens.RadiusSm;

            // balance pills FLOAT above the sheet (the genre-standard "wallet
            // over the wardrobe"), including zero balances for any currency the
            // wardrobe charges in — so "not enough crystals" is never a mystery.
            _balances = new VisualElement();
            _balances.style.position = Position.Absolute;
            _balances.style.left = 0;
            _balances.style.bottom = Length.Percent(100f);
            _balances.style.marginBottom = LvnTokens.Space2;
            ScreenUi.Row(_balances);
            Add(_balances);

            // ОДНА СТРОКА ВМЕСТО ТРЁХ (Илья 26.08): заголовка «Гардероб» нет —
            // и так видно, куда попал; герои переехали колонкой к левому краю,
            // зеркально лицам справа; разделы и «Во весь рост» делят эту
            // строку. Лист от этого стал на две строки ниже — куклу видно
            // больше, а лишнего места не осталось.
            var headRow = ScreenUi.Row();
            Add(headRow);

            _title = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Pick("wardrobe.title", _cfg.title, "Wardrobe"));
            _title.style.display = DisplayStyle.None; // подпись убрана, поле живо для хоста

            // ВО ВЕСЬ РОСТ. Раньше этот шеврон ЗАКРЫВАЛ примерку, и игроки его
            // не понимали: фигура «свернуть вниз» обещает свернуть, а не выйти.
            // Теперь она делает именно обещанное — убирает панель и весь
            // интерфейс, чтобы наряд было видно целиком; примерка при этом не
            // прерывается, любое касание возвращает панель. Выход из примерки
            // переехал вниз, к «Выбрать», отдельной кнопкой «Отменить».
            // «Во весь рост» обязан показать фигуру ЦЕЛИКОМ — зум раздела
            // снимается вместе с панелью; возврат наводит его заново (хост).
            var peek = new Button(() => { FireSectionFocus(null); OnPeek?.Invoke(true); }) { text = "" };
            peek.style.flexShrink = 0; // разделы жмутся, кнопка — никогда
            ScreenUi.Row(peek);
            peek.style.justifyContent = Justify.Center;
            var peekIcon = LvnIcons.Make(LvnIcon.Chevron, 20f, LvnTokens.Text);
            peekIcon.style.rotate = new Rotate(90f);
            peek.Add(peekIcon);
            _peekLabel = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Pick("wardrobe.peek", _cfg.peek_text, "Full height"));
            var peekLabel = _peekLabel;
            peekLabel.style.fontSize = LvnTokens.TextXs;
            peekLabel.style.marginLeft = LvnTokens.Space1;
            peekLabel.style.color = LvnTokens.Text;
            peek.Add(peekLabel);
            LvnAir.Pad(peek, LvnTokens.Space2, LvnTokens.Space1);
            SkinButton(peek, false);

            // Character pills — ONLY the always-open wardrobe shows them, and
            // only when several dressable characters have a collection. A story
            // moment dresses exactly who the author says.
            //
            // Колонка У ЛЕВОГО КРАЯ, поверх куклы (Илья 26.08: «героев надо
            // перечислять слева сбоку, как эмоции»): строка в листе съедала
            // место и повторяла то, что и так читается по кукле.
            _rosterRow = new VisualElement();
            _rosterRow.style.position = Position.Absolute;
            _rosterRow.style.left = 0;
            _rosterRow.style.flexDirection = FlexDirection.Column;
            _rosterRow.style.alignItems = Align.FlexStart;
            _rosterRow.style.display = DisplayStyle.None;
            Add(_rosterRow);

            _tabs = new VisualElement();
            _tabs.style.flexDirection = FlexDirection.Row;
            _tabs.style.justifyContent = Justify.Center;
            _tabs.style.flexGrow = 1;    // разделы занимают строку, кнопка справа
            _tabs.style.flexShrink = 1;
            // РАЗДЕЛЫ — ОДНА СТРОКА. Перенос был разрешён, и в английской
            // локали «Accessories» с «Hairstyle» выталкивали «Outfit» на
            // вторую строку (живой скрин Ильи 29.08): шапка листа прыгала по
            // высоте, лента под ней съезжала, а сам ряд переставал читаться
            // как переключатель — четвёртый раздел выглядел отдельной кнопкой.
            _tabs.style.flexWrap = Wrap.NoWrap;
            _tabs.RegisterCallback<GeometryChangedEvent>(_ => FitTabs());
            headRow.Add(_tabs);
            headRow.Add(peek); // строка: разделы — по центру, «Во весь рост» — справа

            BuildEmotionColumn();

            // ЛЕНТА КАРТОЧЕК СКИНОВ (решение Ильи 27.08: единый гардероб —
            // «взял бы плашку из игры, а карусель слить с карточками»): все
            // варианты оси видны разом — арт, цена ПРЯМО на картинке (бейдж в
            // углу), имя на серой подложке снизу. Тап = примерка; лента и
            // карусель ниже — два руля одного состояния (_index).
            _strip = Lvn.UI.LvnScroll.Horizontal();
            _strip.style.marginTop = LvnTokens.Space2;
            _strip.contentContainer.style.flexDirection = FlexDirection.Row;
            Add(_strip);

            // ПОДНАСТРОЙКА РАЗДЕЛА (Илья 28.08: «прическа и цвет волос по
            // отдельности не нравится»): слот с subOf живёт не своим табом, а
            // рядом круглых свотчей под лентой родителя — причёска и её цвет
            // выбираются в одном разделе.
            _subRow = new VisualElement();
            ScreenUi.Row(_subRow);
            _subRow.style.justifyContent = Justify.Center;
            _subRow.style.marginTop = LvnTokens.Space2;
            _subRow.style.display = DisplayStyle.None;
            Add(_subRow);

            // ◀ item name ▶
            var carousel = ScreenUi.Row();
            carousel.style.marginTop = LvnTokens.Space2;
            Add(carousel);

            var prev = new Button(() => Step(-1)) { text = "" };
            var next = new Button(() => Step(+1)) { text = "" };
            _prevBtn = prev; _nextBtn = next;
            var prevIcon = LvnIcons.Make(LvnIcon.Chevron, 22f, LvnTokens.Text);
            prevIcon.style.rotate = new Rotate(180f);
            prev.Add(prevIcon);
            next.Add(LvnIcons.Make(LvnIcon.Chevron, 22f, LvnTokens.Text));
            foreach (var b in new[] { prev, next })
            {
                b.style.alignItems = Align.Center;
                b.style.justifyContent = Justify.Center;
                LvnAir.Pad(b, LvnTokens.Space3, LvnTokens.Space2);
                SkinButton(b, false);
            }
            carousel.Add(prev);

            _itemName = new Label("");
            _itemName.style.flexGrow = 1;
            _itemName.style.color = _text;
            _itemName.style.fontSize = LvnTokens.TextBase;
            _itemName.style.unityTextAlign = TextAnchor.MiddleCenter;
            _itemName.style.backgroundColor = LvnTokens.Veil(0.35f);
            LvnAir.PadY(_itemName, LvnTokens.Space2);
            LvnAir.MarginX(_itemName, LvnTokens.Space2);
            LvnChrome.Round(_itemName, _radius);
            carousel.Add(_itemName);
            carousel.Add(next);

            // ДВА ЯВНЫХ ВЫХОДА В ОДНОЙ СТРОКЕ: «Отменить» закрывает примерку и
            // НИЧЕГО не сохраняет, «Выбрать» надевает и закрывает. Пока выход
            // был один и прятался шевроном в углу, игрок не понимал ни как
            // выйти, ни сохранится ли надетое.
            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.marginTop = LvnTokens.Space2;
            Add(actions);

            _cancel = Lvn.UI.LvnRedress.Bind(new Button(Cancel),
                () => LvnWords.Pick("wardrobe.cancel", _cfg.cancel_text, "Cancel"));
            _cancel.style.fontSize = LvnTokens.TextBase;
            _cancel.style.flexGrow = 1;
            _cancel.style.flexBasis = 0;
            LvnAir.PadY(_cancel, LvnTokens.Space2);
            _cancel.style.marginRight = LvnTokens.Space2;
            SkinButton(_cancel, false);
            actions.Add(_cancel);

            _confirm = new Button(() => LvnAsync.Fire(ConfirmAsync(), "Confirm"));
            _confirm.style.fontSize = LvnTokens.TextBase;
            _confirm.style.flexGrow = 1;
            _confirm.style.flexBasis = 0;
            LvnAir.PadY(_confirm, LvnTokens.Space2);
            SkinButton(_confirm, true);
            // Цена стоит НА кнопке, поэтому подпись у кнопки составная: слово,
            // число и значок валюты. Собственный text у Button остаётся пустым —
            // иначе он рисовался бы поверх строки.
            _confirmRow = new VisualElement { pickingMode = PickingMode.Ignore };
            ScreenUi.Row(_confirmRow);
            _confirmRow.style.justifyContent = Justify.Center;
            _confirmRow.style.flexGrow = 1;
            _confirmLabel = new Label(string.Empty) { pickingMode = PickingMode.Ignore };
            _confirmLabel.style.fontSize = LvnTokens.TextBase;
            _confirmRow.Add(_confirmLabel);
            _confirm.Add(_confirmRow);
            actions.Add(_confirm);
        }

        private VisualElement _confirmRow;
        private Label _confirmLabel;
        private VisualElement _confirmCoin;




        /// <summary>
        /// Приехал свежий манифест (<see cref="ILvnContentAware"/>).
        ///
        /// <para>Метод назывался <c>SetManifest</c> — своим словом, — и потому
        /// лист не подходил под общую пометку «живу манифестом». Следствие
        /// было не косметическим: соседним экранам содержимое развозил НАБОР
        /// по пометке, а этому листу его вручали по имени, отдельной строкой,
        /// которую надо было не забыть. Именно эту строку однажды и забыли —
        /// вкладка гардероба одна оставалась на прежнем содержимом, пока
        /// соседи показывали новое.</para>
        ///
        /// <para>Одна работа под двумя именами не выглядит дублем: она
        /// выглядит двумя разными работами, и общее правило её просто не
        /// видит.</para>
        /// </summary>
        public void SetContent(LvnManifest manifest) => _manifest = manifest;

        private VisualElement _rosterRow;
        public async Task ShowAsync(string entityId, CancellationToken ct = default)
        {
            if (_open) return;
            _open = true;
            _confirm.SetEnabled(true); // never inherit a dead button from a past session
            BuildFor(entityId);
            // A story wardrobe moment IS the outfits crossing the player's path —
            // everything it offers joins the always-open wardrobe's collection.
            if (!OnlySeen && MarkSeenOnShow && _def?.wardrobe != null)
                foreach (var kv in _def.wardrobe)
                    if (kv.Value?.items != null)
                        foreach (var it in kv.Value.items)
                            if (it != null && !string.IsNullOrEmpty(it.value))
                                LvnWardrobe.MarkSeen(_entity, kv.Key, it.value);
            RefreshBalances();
            LvnWallet.Changed += OnWalletChanged;
            LvnAsync.Fire(LvnWallet.NudgeAsync(), "Refresh");
            try { await _gate.WaitAsync(ct); }
            finally
            {
                LvnWallet.Changed -= OnWalletChanged;
                LvnWardrobe.ClearPreview(_entity); // confirm stays; cancel blends back
                _open = false;
            }
        }

        public void Hide()
        {
            LvnWallet.Changed -= OnWalletChanged;
            if (!string.IsNullOrEmpty(_entity)) LvnWardrobe.ClearPreview(_entity);
            _open = false;
            FireSectionFocus(null); // жёсткое закрытие возвращает общий план
            _gate.Release(false);
        }

        private void Cancel() => _gate.Release(false);

        // Every currency the wardrobe charges in + everything the player holds.
        private void RefreshBalances()
        {
            _balances.Clear();
            _balances.style.display = HideBalances ? DisplayStyle.None : DisplayStyle.Flex;
            if (HideBalances) return;
            var currencies = new List<string>();
            if (_def?.wardrobe != null)
                foreach (var slot in _def.wardrobe.Values)
                    if (slot?.items != null)
                        foreach (var it in slot.items)
                            if (it != null && it.price > 0 && !string.IsNullOrEmpty(it.currency)
                                && !currencies.Contains(it.currency))
                                currencies.Add(it.currency);
            foreach (var kv in LvnWallet.Balances)
                if (!currencies.Contains(kv.Key)) currencies.Add(kv.Key);

            foreach (var cur in currencies)
            {
                // Плашка — общий компонент оболочки: здесь только метрика листа
                // и кнопка «+». Раньше лист собирал её сам и единственный во
                // всей игре писал вместо значка служебное имя валюты.
                _balances.Add(new LvnWalletPill(cur, new LvnWalletPill.Look
                {
                    MarginLeft = 0,
                    PadLeft = 12, PadRight = 6,
                    Radius = 16f,
                    IconSize = 24f,
                    FontSize = 22f,
                    Background = UiColor.Named(_cfg.panel_color, new Color(0.078f, 0.078f, 0.10f, 0.97f)),
                    TextColor = _text,
                    IconUrl = _cfg.currency_icons != null
                              && _cfg.currency_icons.TryGetValue(cur, out var url) ? url : null,
                }, _assets, onPlus: OpenStore != null ? () => Lvn.LvnAsync.Fire(OpenStore(), "OpenStore") : (System.Action)null)
                { style = { marginRight = 8 } });
            }
        }

        /// <summary>Build tabs/carousel for a character. Public for tests.</summary>
        public void BuildFor(string entityId)
        {
            _entity = entityId;
            if ((string.IsNullOrEmpty(_entity) || _manifest?.sprites == null
                 || !_manifest.sprites.ContainsKey(_entity)) && _manifest?.sprites != null)
            {
                foreach (var kv in _manifest.sprites) // fallback: first with a wardrobe
                    if (kv.Value?.wardrobe != null && kv.Value.wardrobe.Count > 0) { _entity = kv.Key; break; }
            }
            _def = _entity != null && _manifest?.sprites != null
                   && _manifest.sprites.TryGetValue(_entity, out var d) ? d : null;
            _index.Clear();
            _autoDressed.Clear(); // лист собирается заново — и его примерки тоже
            _tabs.Clear();
            _tabFit = 0;   // ряд собирается заново — меряем с чистого листа
            _tab = null;
            _title.text = LvnWords.Pick("wardrobe.title", _cfg.title, "Wardrobe");

            RebuildRoster();
            if (_def?.wardrobe == null || _def.wardrobe.Count == 0)
            {
                _itemName.text = LvnWords.Pick("wardrobe.empty", _cfg.empty_text, "The wardrobe is empty");
                RebuildStrip(); // не показывать карточки прошлого персонажа
                RefreshConfirm();
                return;
            }

            foreach (var kv in _def.wardrobe)
            {
                var axis = kv.Key;
                if (Items(axis).Count == 0) continue; // nothing collected here yet
                if (IsSubAxis(axis)) continue; // поднастройка живёт в табе родителя
                if (_tab == null) _tab = axis;
                var slot = kv.Value;
                // ПИЛЮЛЯ В ОДНУ СТРОКУ С ИКОНКОЙ (Илья 27.08): квадрат 92×92
                // ломал «Платье» переносом («Плать/е», живой скрин). Иконка —
                // из манифеста (slot.icon), иначе вектор по смыслу оси: волосы —
                // корона, наряд — вешалка.
                var b = new Button(() => SelectTab(axis)) { text = "" };
                b.style.height = LvnTokens.TouchLg;
                ScreenUi.Row(b);
                LvnAir.PadX(b, LvnTokens.Space3);
                LvnAir.MarginX(b, LvnTokens.Space1);
                LvnChrome.Round(b, LvnTokens.RadiusLg);
                Smooth(b, LvnMotion.Normal, "background-color", "border-top-color",
                    "border-right-color", "border-bottom-color", "border-left-color");
                b.userData = axis;
                if (!string.IsNullOrEmpty(slot?.icon))
                {
                    var img = new VisualElement { pickingMode = PickingMode.Ignore };
                    img.name = "ax-art";
                    img.style.width = 30; img.style.height = 30;
                    img.style.marginRight = LvnTokens.Space2;
                    LvnPicture.Photo(img, slot.icon, _assets, cover: false);
                    b.Add(img);
                }
                else
                {
                    var icon = LvnWardrobeStage.IconFor(axis);
                    // Два глифа под оба фона пилюли: SelectTab переключает их
                    // display (вектор не перекрашивается на месте).
                    var off = LvnIcons.Make(icon, 24f, _text);
                    off.name = "ax-ic-off"; off.pickingMode = PickingMode.Ignore;
                    off.style.marginRight = LvnTokens.Space2;
                    var on = LvnIcons.Make(icon, 24f, _accentText);
                    on.name = "ax-ic-on"; on.pickingMode = PickingMode.Ignore;
                    on.style.marginRight = LvnTokens.Space2;
                    on.style.display = DisplayStyle.None;
                    b.Add(off); b.Add(on);
                }
                // Подпись оси — из словаря: «Украшения» и «Причёска» в
                // английском интерфейсе выглядят как недоделанный перевод.
                var lbl = new Label { pickingMode = PickingMode.Ignore };
                var ax = axis; var slotName = slot?.name;
                Lvn.UI.LvnRedress.Bind(lbl, () => Lvn.Content.LvnWords.Name("axis", ax, slotName));
                lbl.name = "ax-label";
                lbl.style.fontSize = LvnTokens.TextSm;
                lbl.style.whiteSpace = WhiteSpace.NoWrap;
                lbl.style.color = _text;
                Smooth(lbl, LvnMotion.Normal, "color");
                b.Add(lbl);
                _tabs.Add(b);
            }

            // «ВСЕ» (Илья 27.08): купленные скины со всех осей одной витриной,
            // кадр без зума — вся фигура.
            if (_def.wardrobe.Count > 1)
            {
                var all = new Button(() => SelectTab(AllTab)) { text = "" };
                all.style.height = LvnTokens.TouchLg;
                ScreenUi.Row(all);
                LvnAir.PadX(all, LvnTokens.Space3);
                LvnAir.MarginX(all, LvnTokens.Space1);
                LvnChrome.Round(all, LvnTokens.RadiusLg);
                Smooth(all, LvnMotion.Normal, "background-color", "border-top-color",
                    "border-right-color", "border-bottom-color", "border-left-color");
                all.userData = AllTab;
                var offA = LvnIcons.Make(LvnIcon.Star, 24f, _text);
                offA.name = "ax-ic-off"; offA.pickingMode = PickingMode.Ignore;
                offA.style.marginRight = LvnTokens.Space2;
                var onA = LvnIcons.Make(LvnIcon.Star, 24f, _accentText);
                onA.name = "ax-ic-on"; onA.pickingMode = PickingMode.Ignore;
                onA.style.marginRight = LvnTokens.Space2;
                onA.style.display = DisplayStyle.None;
                all.Add(offA); all.Add(onA);
                var lblA = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("wardrobe.mine", "Mine"));
                lblA.pickingMode = PickingMode.Ignore;
                lblA.name = "ax-label";
                lblA.style.fontSize = LvnTokens.TextSm;
                lblA.style.whiteSpace = WhiteSpace.NoWrap;
                lblA.style.color = _text;
                Smooth(lblA, LvnMotion.Normal, "color");
                all.Add(lblA);
                _tabs.Insert(0, all); // «Моё» — первым (Илья 28.08)
            }

            // The hero must OPEN the sheet already dressed from THIS sheet: an
            // axis whose worn value isn't among the scene's items puts on its
            // first OWNED item right away, for every axis — not just the active
            // tab. Otherwise she stands in last chapter's (possibly retired)
            // outfit until the player taps that tab, and the swap lands as a jump.
            //
            // Три условия, каждое закрывает свою щель. Надетым считается и
            // дефолт каталога — иначе у игрока, который ещё ничего не выбирал,
            // «надето» пусто, и лист переодевал героиню при каждом открытии.
            // Берётся первый предмет, которым игрок ВЛАДЕЕТ: примерять
            // неоплаченное лист не вправе. И примерка ПОМЕЧАЕТСЯ системной
            // (_autoDressed): подтверждать в ней нечего, пока игрок сам ничего
            // не выбрал, — иначе «Выбрать» и «Отменить» горят всегда, хотя он
            // только открыл гардероб (живой скрин 27.08). Надеть по-настоящему
            // тоже нельзя: покупка и выбор — раздельные акты, а тихий equip на
            // открытии листа сделал бы выбор за игрока.
            foreach (var kv in _def.wardrobe)
            {
                var axis = kv.Key;
                var items = Items(axis);
                if (items.Count == 0) continue;
                var worn = LvnCostumer.Chosen(_entity, axis, _def.defaults);
                // Съёмный слот без дефолта (украшения): «ничего не надето» —
                // это и есть пункт «Нет», а не пробел, который надо заполнить.
                if (string.IsNullOrEmpty(worn) && items.Count > 0 && items[0].value == LvnWardrobe.NoneValue)
                    worn = LvnWardrobe.NoneValue;
                bool inList = false;
                foreach (var it in items)
                    if (it.value == worn) { inList = true; break; }
                if (inList) continue;
                int owned = -1;
                for (int n = 0; n < items.Count && owned < 0; n++)
                    if (IsOwnedIn(axis, items[n])) owned = n;
                if (owned < 0) continue; // всё платное — пусть остаётся как есть
                _index[axis] = owned;
                LvnLog.Trace($"[lvn-wardrobe] лист одевает {_entity}.{axis}: '{worn ?? "-"}' не из этого листа → '{items[owned].value}'");
                LvnWardrobe.Preview(_entity, axis, items[owned].value);
                _autoDressed[axis] = items[owned].value;
            }

            RebuildEmotions();
            // «Моё» — вкладка по умолчанию (Илья 28.08), когда она есть.
            SelectTab(_def.wardrobe.Count > 1 ? AllTab : _tab);
        }


        private static readonly string[] CardGlide = LvnMotion.CardGlide;


        // ── поднастройки: ось-уточнение внутри таба родителя ─────────────────
        // Слот с subOf (цвет волос → subOf:"hairstyle") своего таба не имеет:
        // он рисуется рядом круглых свотчей под лентой родительского раздела.
        private void RefreshLabel()
        {
            if (_itemName == null || _tab == null) return;
            if (_tab == AllTab)
            {
                // «Моё» подписывается выбранной основой, а не собой.
                var basis = AllTabAxis;
                if (basis == null) return;
                var slot = _def.wardrobe[basis];
                var val = CurrentValueOf(basis);
                var nm = NameOfValue(basis, val);
                // Строка выбора собирается из ДВУХ подписей — оси и значения, — и
                // обе идут через словарь: иначе «Основа: Запад» остаётся русским
                // посреди английского гардероба.
                var axisName = Lvn.Content.LvnWords.Name("axis", basis, slot?.name);
                _itemName.text = string.IsNullOrEmpty(nm)
                    ? axisName
                    : axisName + ": " + Lvn.Content.LvnWords.Name("skin", val, nm);
                return;
            }
            var item = CurrentItem();
            if (item == null) return;
            var name = Lvn.Content.LvnWords.Name("skin", item.value, item.name);
            // ПЕРВЫМ — ТО, ЧТО ВЫБРАНО В ЛЕНТЕ. Подпись собиралась наоборот
            // («Шатенка: Голливудские волны»), и читалась как «предмет:
            // уточнение» — то есть ровно шиворот-навыворот: начиналась словом,
            // которого в ленте нет вовсе, потому что цвет волос выбирают
            // свотчами под ней (живой репорт Ильи 29.08). Уточнения идут
            // после, через точку-разделитель, а не через двоеточие: двоеточие
            // обещает, что слева — заголовок.
            string extra = null;
            foreach (var sub in SubAxesOf(_tab))
            {
                var v = CurrentValueOf(sub);
                if (string.IsNullOrEmpty(v)) continue;
                var n = NameOfValue(sub, v);
                if (string.IsNullOrEmpty(n)) continue;
                n = Lvn.Content.LvnWords.Name("skin", v, n);
                extra = extra == null ? n : extra + ", " + n;
            }
            _itemName.text = extra == null ? name : name + " · " + extra;
        }

        // Стрелки листают КАРУСЕЛЬ раздела; на вкладке «Моё» каруселью служит
        // основа фигуры. Кнопка, которая ничего не делает, просто врёт —
        // поэтому включается ровно тогда, когда есть что листать.
        private void RefreshArrows()
        {
            var axis = _tab == AllTab ? AllTabAxis : _tab;
            bool on = axis != null && Items(axis).Count > 1;
            foreach (var b in new[] { _prevBtn, _nextBtn })
            {
                if (b == null) continue;
                b.SetEnabled(on);
                b.style.opacity = on ? 1f : 0.35f;
            }
        }

        // Choice-button skin: the same fill/text/art the story's choices use,
        // so the wardrobe's controls read as the game's own buttons.
        private void SkinButton(Button b, bool accent)
        {
            LvnStyler.Skinned(b,
                accent ? _accent : UiColor.Named(_ch?.color, LvnTokens.Faint),
                accent ? _accentText : UiColor.Named(_ch?.text_color, _text),
                _ch?.corner_radius ?? _radius);
            if (!accent && !string.IsNullOrEmpty(_ch?.button_image))
                LvnAsync.Fire(Lvn.UI.LvnPicture.Frame(b, _ch.button_image, _ch.button_slice ?? 0, _assets), "ApplyNineSlice");
            else
                b.style.backgroundImage = new StyleBackground(StyleKeyword.None); // an accent tab drops the art
        }
    }
}
