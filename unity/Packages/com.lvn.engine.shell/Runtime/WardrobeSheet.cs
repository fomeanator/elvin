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
    public sealed partial class WardrobeSheet : VisualElement
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
            _text = UiColor.Parse(_cfg.text_color ?? _dlg?.text_color, new Color(0.95f, 0.93f, 0.88f));
            _dim = UiColor.Parse(_cfg.dim_text_color, LvnTokens.TextDim);
            _accent = UiColor.Parse(_cfg.accent_color ?? _dlg?.speaker_color, LvnTokens.Accent);
            _accentText = UiColor.Parse(_cfg.accent_text_color, LvnTokens.OnAccent);
            _radius = _cfg.corner_radius ?? _dlg?.corner_radius ?? 12f;

            // balance pills FLOAT above the sheet (the genre-standard "wallet
            // over the wardrobe"), including zero balances for any currency the
            // wardrobe charges in — so "not enough crystals" is never a mystery.
            _balances = new VisualElement();
            _balances.style.position = Position.Absolute;
            _balances.style.left = 0;
            _balances.style.bottom = Length.Percent(100f);
            _balances.style.marginBottom = 10;
            _balances.style.flexDirection = FlexDirection.Row;
            _balances.style.alignItems = Align.Center;
            Add(_balances);

            // ОДНА СТРОКА ВМЕСТО ТРЁХ (Илья 26.08): заголовка «Гардероб» нет —
            // и так видно, куда попал; герои переехали колонкой к левому краю,
            // зеркально лицам справа; разделы и «Во весь рост» делят эту
            // строку. Лист от этого стал на две строки ниже — куклу видно
            // больше, а лишнего места не осталось.
            var headRow = new VisualElement();
            headRow.style.flexDirection = FlexDirection.Row;
            headRow.style.alignItems = Align.Center;
            Add(headRow);

            _title = new Label(_cfg.title ?? LvnWords.Of("wardrobe.title", "Wardrobe"));
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
            peek.style.flexDirection = FlexDirection.Row;
            peek.style.alignItems = Align.Center;
            peek.style.justifyContent = Justify.Center;
            var peekIcon = LvnIcons.Make(LvnIcon.Chevron, 20f, LvnTokens.Text);
            peekIcon.style.rotate = new Rotate(90f);
            peek.Add(peekIcon);
            var peekLabel = new Label(_cfg.peek_text ?? LvnWords.Of("wardrobe.peek", "Full height"));
            peekLabel.style.fontSize = 20;
            peekLabel.style.marginLeft = 8;
            peekLabel.style.color = LvnTokens.Text;
            peek.Add(peekLabel);
            peek.style.paddingLeft = 14; peek.style.paddingRight = 14;
            peek.style.paddingTop = 6; peek.style.paddingBottom = 6;
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
            _tabs.style.flexWrap = Wrap.Wrap;
            headRow.Add(_tabs);
            headRow.Add(peek); // строка: разделы — по центру, «Во весь рост» — справа

            // БАБЛИКИ ЭМОЦИЙ (идея Ильи 27.08 — «уникальная штука»): колонка
            // лиц СПРАВА ОТ ГЕРОИНИ, над листом (как пилюли кошелька слева —
            // тот же приём bottom:100%). Тап примеряет эмоцию на живую куклу
            // через Preview оси `emotion`. В гардеробные слоты ось не входит —
            // «Выбрать» её не коммитит, закрытие листа возвращает лицо по
            // умолчанию. Горизонтальный ряд в листе «странно скроллился»
            // (живой репорт) — вертикаль у правого края читается сама.
            _emotions = new ScrollView(ScrollViewMode.Vertical);
            _emotions.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _emotions.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _emotions.style.position = Position.Absolute;
            _emotions.style.right = EmoBarLane; // полоса у самого края — под индикатор
            _emotions.style.display = DisplayStyle.None;
            _emotions.contentContainer.style.alignItems = Align.FlexEnd;
            MakeDragScrollable(_emotions); // тянется рукой, а не только колесом
            Add(_emotions);

            // ГДЕ МЫ В СПИСКЕ ЛИЦ (Илья 26.08: «показывать кружками скролл —
            // полупрозрачными прямоугольниками модными, справа место есть»):
            // сегментированная дорожка у правого края, по ней плавно скользит
            // бегунок. Своя, а не штатный скроллбар: колонка живёт поверх
            // куклы, и серая полоса Unity выбивалась бы из оболочки.
            _emoBar = new VisualElement { pickingMode = PickingMode.Ignore };
            _emoBar.style.position = Position.Absolute;
            _emoBar.style.right = 4;
            _emoBar.style.width = EmoBarWidth;
            _emoBar.style.display = DisplayStyle.None;
            Add(_emoBar);
            for (int s = 0; s < EmoBarSegments; s++)
            {
                var seg = new VisualElement { pickingMode = PickingMode.Ignore };
                seg.style.flexGrow = 1;
                seg.style.marginBottom = s == EmoBarSegments - 1 ? 0 : 4;
                seg.style.backgroundColor = new Color(1f, 1f, 1f, 0.13f);
                LvnChrome.Round(seg, EmoBarWidth * 0.5f);
                _emoBar.Add(seg);
            }
            _emoThumb = new VisualElement { pickingMode = PickingMode.Ignore };
            _emoThumb.style.position = Position.Absolute;
            _emoThumb.style.left = 0; _emoThumb.style.right = 0;
            _emoThumb.style.backgroundColor = new Color(1f, 1f, 1f, 0.62f);
            LvnChrome.Round(_emoThumb, EmoBarWidth * 0.5f);
            Smooth(_emoThumb, LvnMotion.Quick, "top", "height");
            _emoBar.Add(_emoThumb);
            // Скроллеры спрятаны, но живут — их значение и есть позиция.
            _emotions.verticalScroller.valueChanged += _ => UpdateEmoScrollBar();
            _emotions.RegisterCallback<GeometryChangedEvent>(_ => UpdateEmoScrollBar());
            // ПОД НАВБАРОМ (Илья 28.08: «баблы перекрываются — по топу, под
            // навбаром лучше»): колонка, растущая от плашки вверх, наезжала на
            // неё, когда лиц больше, чем зазора. Теперь верх колонки прибит к
            // низу навбара, а высота ограничена зазором до плашки — лишнее
            // скроллится внутри, перекрытий не бывает по построению.
            RegisterCallback<GeometryChangedEvent>(_ => PlaceEmotions());

            // ЛЕНТА КАРТОЧЕК СКИНОВ (решение Ильи 27.08: единый гардероб —
            // «взял бы плашку из игры, а карусель слить с карточками»): все
            // варианты оси видны разом — арт, цена ПРЯМО на картинке (бейдж в
            // углу), имя на серой подложке снизу. Тап = примерка; лента и
            // карусель ниже — два руля одного состояния (_index).
            _strip = new ScrollView(ScrollViewMode.Horizontal);
            _strip.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _strip.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _strip.style.marginTop = 12;
            _strip.contentContainer.style.flexDirection = FlexDirection.Row;
            Add(_strip);

            // ПОДНАСТРОЙКА РАЗДЕЛА (Илья 28.08: «прическа и цвет волос по
            // отдельности не нравится»): слот с subOf живёт не своим табом, а
            // рядом круглых свотчей под лентой родителя — причёска и её цвет
            // выбираются в одном разделе.
            _subRow = new VisualElement();
            _subRow.style.flexDirection = FlexDirection.Row;
            _subRow.style.alignItems = Align.Center;
            _subRow.style.justifyContent = Justify.Center;
            _subRow.style.marginTop = 10;
            _subRow.style.display = DisplayStyle.None;
            Add(_subRow);

            // ◀ item name ▶
            var carousel = new VisualElement();
            carousel.style.flexDirection = FlexDirection.Row;
            carousel.style.alignItems = Align.Center;
            carousel.style.marginTop = 12;
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
                b.style.paddingLeft = 16; b.style.paddingRight = 16;
                b.style.paddingTop = 10; b.style.paddingBottom = 10;
                SkinButton(b, false);
            }
            carousel.Add(prev);

            _itemName = new Label("");
            _itemName.style.flexGrow = 1;
            _itemName.style.color = _text;
            _itemName.style.fontSize = 28;
            _itemName.style.unityTextAlign = TextAnchor.MiddleCenter;
            _itemName.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
            _itemName.style.marginLeft = 10; _itemName.style.marginRight = 10;
            _itemName.style.paddingTop = 10; _itemName.style.paddingBottom = 10;
            LvnChrome.Round(_itemName, _radius);
            carousel.Add(_itemName);
            carousel.Add(next);

            // ДВА ЯВНЫХ ВЫХОДА В ОДНОЙ СТРОКЕ: «Отменить» закрывает примерку и
            // НИЧЕГО не сохраняет, «Выбрать» надевает и закрывает. Пока выход
            // был один и прятался шевроном в углу, игрок не понимал ни как
            // выйти, ни сохранится ли надетое.
            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.marginTop = 12;
            Add(actions);

            _cancel = new Button(Cancel) { text = _cfg.cancel_text ?? LvnWords.Of("wardrobe.cancel", "Cancel") };
            _cancel.style.fontSize = 28;
            _cancel.style.flexGrow = 1;
            _cancel.style.flexBasis = 0;
            _cancel.style.marginRight = 10;
            _cancel.style.paddingTop = 14;
            _cancel.style.paddingBottom = 14;
            SkinButton(_cancel, false);
            actions.Add(_cancel);

            _confirm = new Button(() => LvnAsync.Fire(ConfirmAsync(), "Confirm"));
            _confirm.style.fontSize = 28;
            _confirm.style.flexGrow = 1;
            _confirm.style.flexBasis = 0;
            _confirm.style.paddingTop = 14;
            _confirm.style.paddingBottom = 14;
            SkinButton(_confirm, true);
            // Цена стоит НА кнопке, поэтому подпись у кнопки составная: слово,
            // число и значок валюты. Собственный text у Button остаётся пустым —
            // иначе он рисовался бы поверх строки.
            _confirmRow = new VisualElement { pickingMode = PickingMode.Ignore };
            _confirmRow.style.flexDirection = FlexDirection.Row;
            _confirmRow.style.justifyContent = Justify.Center;
            _confirmRow.style.alignItems = Align.Center;
            _confirmRow.style.flexGrow = 1;
            _confirmLabel = new Label(string.Empty) { pickingMode = PickingMode.Ignore };
            _confirmLabel.style.fontSize = 28;
            _confirmRow.Add(_confirmLabel);
            _confirm.Add(_confirmRow);
            actions.Add(_confirm);
        }

        private VisualElement _confirmRow;
        private Label _confirmLabel;
        private VisualElement _confirmCoin;




        public void SetManifest(LvnManifest manifest) => _manifest = manifest;

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
            LvnAsync.Fire(LvnWallet.RefreshAsync(), "Refresh");
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
                    Background = UiColor.Parse(_cfg.panel_color, new Color(0.078f, 0.078f, 0.10f, 0.97f)),
                    TextColor = _text,
                    IconUrl = _cfg.currency_icons != null
                              && _cfg.currency_icons.TryGetValue(cur, out var url) ? url : null,
                }, _assets, onPlus: OpenStore != null ? () => _ = OpenStore() : (System.Action)null)
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
            _tab = null;
            _title.text = _cfg.title ?? LvnWords.Of("wardrobe.title", "Wardrobe");

            RebuildRoster();
            if (_def?.wardrobe == null || _def.wardrobe.Count == 0)
            {
                _itemName.text = _cfg.empty_text ?? LvnWords.Of("wardrobe.empty", "The wardrobe is empty");
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
                b.style.height = 56;
                b.style.flexDirection = FlexDirection.Row;
                b.style.alignItems = Align.Center;
                b.style.marginLeft = 6; b.style.marginRight = 6;
                b.style.paddingLeft = 18; b.style.paddingRight = 20;
                LvnChrome.Round(b, 28f);
                Smooth(b, LvnMotion.Normal, "background-color", "border-top-color",
                    "border-right-color", "border-bottom-color", "border-left-color");
                b.userData = axis;
                if (!string.IsNullOrEmpty(slot?.icon))
                {
                    var img = new VisualElement { pickingMode = PickingMode.Ignore };
                    img.style.width = 30; img.style.height = 30;
                    img.style.marginRight = 10;
                    img.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                    LvnAsync.Fire(ScreenUi.AssignBgAsync(img, slot.icon, _assets), "AssignBg");
                    b.Add(img);
                }
                else
                {
                    var icon = LvnWardrobeStage.IconFor(axis);
                    // Два глифа под оба фона пилюли: SelectTab переключает их
                    // display (вектор не перекрашивается на месте).
                    var off = LvnIcons.Make(icon, 24f, _text);
                    off.name = "ax-ic-off"; off.pickingMode = PickingMode.Ignore;
                    off.style.marginRight = 10;
                    var on = LvnIcons.Make(icon, 24f, _accentText);
                    on.name = "ax-ic-on"; on.pickingMode = PickingMode.Ignore;
                    on.style.marginRight = 10;
                    on.style.display = DisplayStyle.None;
                    b.Add(off); b.Add(on);
                }
                var lbl = new Label(slot?.name ?? axis) { pickingMode = PickingMode.Ignore };
                lbl.name = "ax-label";
                lbl.style.fontSize = 22;
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
                all.style.height = 56;
                all.style.flexDirection = FlexDirection.Row;
                all.style.alignItems = Align.Center;
                all.style.marginLeft = 6; all.style.marginRight = 6;
                all.style.paddingLeft = 18; all.style.paddingRight = 20;
                LvnChrome.Round(all, 28f);
                Smooth(all, LvnMotion.Normal, "background-color", "border-top-color",
                    "border-right-color", "border-bottom-color", "border-left-color");
                all.userData = AllTab;
                var offA = LvnIcons.Make(LvnIcon.Star, 24f, _text);
                offA.name = "ax-ic-off"; offA.pickingMode = PickingMode.Ignore;
                offA.style.marginRight = 10;
                var onA = LvnIcons.Make(LvnIcon.Star, 24f, _accentText);
                onA.name = "ax-ic-on"; onA.pickingMode = PickingMode.Ignore;
                onA.style.marginRight = 10;
                onA.style.display = DisplayStyle.None;
                all.Add(offA); all.Add(onA);
                var lblA = new Label(LvnWords.Of("wardrobe.mine", "Mine")) { pickingMode = PickingMode.Ignore };
                lblA.name = "ax-label";
                lblA.style.fontSize = 22;
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
                _itemName.text = string.IsNullOrEmpty(nm)
                    ? (slot?.name ?? basis)
                    : (slot?.name ?? basis) + ": " + nm;
                return;
            }
            var item = CurrentItem();
            if (item == null) return;
            var name = item.name ?? item.value;
            string prefix = null;
            foreach (var sub in SubAxesOf(_tab))
            {
                var v = CurrentValueOf(sub);
                if (string.IsNullOrEmpty(v)) continue;
                var n = NameOfValue(sub, v);
                if (string.IsNullOrEmpty(n)) continue;
                prefix = prefix == null ? n : prefix + ", " + n;
            }
            _itemName.text = prefix == null ? name : prefix + ": " + name;
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

        // Шаблонные иконки: `{ось}` в пути арта подменяется ТЕКУЩИМ значением
        // этой оси (превью → надетое → дефолт → первый предмет). Так карточки
        // причёсок показывают выбранный цвет: hair_rose_{hair} → hair_rose_black.
        private string ResolveIcon(string icon)
        {
            if (string.IsNullOrEmpty(icon) || icon.IndexOf('{') < 0
                || _def?.wardrobe == null) return icon;
            foreach (var kv in _def.wardrobe)
            {
                var token = "{" + kv.Key + "}";
                if (!icon.Contains(token)) continue;
                icon = icon.Replace(token, CurrentValueOf(kv.Key));
            }
            return icon;
        }

        // Ряд поднастроек активного раздела: подпись слота + свотчи. Тап
        // примеряет на живую куклу и перестраивает ленту — шаблонные иконки
        // карточек сразу показывают, например, новый цвет волос.
        private void RebuildSubRow(bool animate = true)
        {
            if (_subRow == null) return;
            _subRow.Clear();
            bool any = false;
            // Вкладка «Моё» тоже носит свои поднастройки — там живёт основа
            // фигуры (запад/север). Раньше ряд молчал на ней целиком.
            if (_tab != null)
                foreach (var sub in SubAxesOf(_tab))
                {
                    var items = Items(sub);
                    if (items.Count == 0) continue;
                    var slot = _def.wardrobe[sub];
                    var lbl = new Label(slot?.name ?? sub);
                    lbl.style.color = _dim;
                    lbl.style.fontSize = 21;
                    lbl.style.marginLeft = any ? 22 : 0; // зазор между слотами
                    lbl.style.marginRight = 10;
                    _subRow.Add(lbl);
                    int n = 0;
                    foreach (var it in items)
                    {
                        var sw = SubSwatch(sub, it);
                        _subRow.Add(sw);
                        if (animate) EnterSoft(sw, n);
                        n++;
                    }
                    any = true;
                }
            _subRow.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // Свотч: круг цвета из манифеста (item.color), иначе — мини-арт;
        // непринадлежащий помечен «◆», текущий обведён акцентом. Покупку
        // непринадлежащего цвета предлагает кнопка «Выбрать» (PendingBuy).
        private VisualElement SubSwatch(string axis, LvnWardrobeItem item)
        {
            bool worn = IsWornIn(axis, item.value);
            bool none = item.value == LvnWardrobe.NoneValue;
            var b = new Button(() =>
            {
                LvnWardrobe.Preview(_entity, axis, item.value); // NoneValue = «снять»
                RebuildStrip(animate: false); // свотчи + шаблонные иконки карточек
                RefreshLabel();   // «Рыжая: Голливудские волны» — цвет И причёска
                RefreshConfirm(); // кнопка предложит купить этот цвет
            }) { text = "" };
            b.style.width = 54; b.style.height = 54;
            b.style.marginLeft = 5; b.style.marginRight = 5;
            b.style.paddingLeft = 0; b.style.paddingRight = 0;
            b.style.paddingTop = 0; b.style.paddingBottom = 0;
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            LvnChrome.Round(b, 27f);
            var fallback = new Color(0.30f, 0.31f, 0.35f, 0.9f);
            b.style.backgroundColor = string.IsNullOrEmpty(item.color)
                ? fallback : UiColor.Parse(item.color, fallback);
            if (none)
                b.Add(LvnIcons.Make(LvnIcon.Close, 22f, _text));
            else if (string.IsNullOrEmpty(item.color) && !string.IsNullOrEmpty(item.icon))
            {
                var art = new VisualElement { pickingMode = PickingMode.Ignore };
                art.style.width = 44; art.style.height = 44;
                art.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                LvnAsync.Fire(AssignCardArtAsync(art, new VisualElement(),
                    ResolveIcon(item.icon)), "WardrobeSwatch");
                b.Add(art);
            }
            LvnChrome.Border(b, worn ? _accent : new Color(1f, 1f, 1f, 0.28f),
                worn ? 3f : 1.5f);
            if (!IsOwnedIn(axis, item))
            {
                var dot = new Label("◆") { pickingMode = PickingMode.Ignore };
                dot.style.position = Position.Absolute;
                dot.style.top = -6; dot.style.right = -6;
                dot.style.color = LvnTokens.Gold;
                dot.style.fontSize = 17;
                b.Add(dot);
            }
            return b;
        }






        private void SelectTab(string axis)
        {
            _tab = axis;
            // Камера хоста едет к зоне раздела: причёска — к голове, украшения
            // — к шее, платье — к корпусу; «Моё» — лёгкий наезд по центру.
            FireSectionFocus(_tab);
            foreach (var c in _tabs.Children())
            {
                var b = c as Button;
                if (b == null) continue;
                bool active = (string)b.userData == _tab;
                SkinButton(b, active);
                LvnChrome.Border(b, active ? _accent : new Color(1f, 1f, 1f, 0.15f), 2f);
                // Дети пилюли (лейбл и пара глифов) красятся вручную — кнопочный
                // skin их не достаёт.
                var lbl = b.Q<Label>("ax-label");
                if (lbl != null) lbl.style.color = active ? _accentText : _text;
                var icOff = b.Q<VisualElement>("ax-ic-off");
                var icOn = b.Q<VisualElement>("ax-ic-on");
                if (icOff != null) icOff.style.display = active ? DisplayStyle.None : DisplayStyle.Flex;
                if (icOn != null) icOn.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
            }

            var items = Items(_tab);
            if (items.Count == 0)
            {
                _itemName.text = "";
                RebuildStrip();
                RefreshConfirm();
                return;
            }
            if (!_index.ContainsKey(_tab))
            {
                // start the carousel on what's worn (previewed beats equipped;
                // ничего не выбирали — надет дефолт каталога, и карусель должна
                // открыться на нём, иначе показ примеряет чужое)
                var current = LvnCostumer.Chosen(_entity, _tab, _def?.defaults);
                int at = 0;
                for (int i = 0; i < items.Count; i++) if (items[i].value == current) { at = i; break; }
                _index[_tab] = at;
            }
            RebuildStrip();
            ShowItem(); // also previews it, so the carousel and the actor agree
        }

        // ── лента карточек: второй руль карусели ─────────────────────────────
        /// <summary>Сборный таб «Моё»: купленные скины со всех осей одной
        /// лентой. Публичен: камера хоста узнаёт его в SectionFocus (лёгкий
        /// наезд вместо общего плана). Значение — из витрины гардероба: кадр
        /// для этой вкладки выбирается там же.</summary>
        public const string AllTab = LvnWardrobeStage.AllAxis;












        /// <summary>Лист живёт ВКЛАДКОЙ (уйти можно навбаром), а не модалкой.
        /// Тогда «Отменить» — не единственный выход, и его честно гасить,
        /// когда отменять нечего; в сюжетном листе он гаснуть не смеет.</summary>
        public bool TabMode;








        private List<LvnWardrobeItem> Items(string axis)
        {
            var list = new List<LvnWardrobeItem>();
            if (axis != null && _def?.wardrobe != null
                && _def.wardrobe.TryGetValue(axis, out var slot) && slot?.items != null)
            {
                // Съёмный слот (украшения) открывается пунктом «Нет»: снятие —
                // такой же выбор, с примеркой (NoneValue) и коммитом (Equip
                // трактует его как «снять»). Просьба Ильи 27.08.
                if (slot.removable == true)
                    list.Add(new LvnWardrobeItem { value = LvnWardrobe.NoneValue, name = LvnWords.Of("wardrobe.none", "None") });
                foreach (var it in slot.items)
                    if (it != null && !string.IsNullOrEmpty(it.value)
                        && (!OnlySeen || Encountered(axis, it.value)))
                        list.Add(it);
            }
            // ТЕКУЩЕЕ — ПЕРВЫМ (Илья: «по умолчанию должно отображаться
            // первым, а не скакать при входе»): надетое (или дефолт; для
            // съёмного пустого — «Нет») переезжает в голову списка. Порядок
            // стабилен на всю примерку — Equip меняет его только по «Выбрать».
            // ЗАФИКСИРОВАННОЕ, а не примеренное: порядок ленты обязан стоять
            // на месте, пока игрок крутит варианты.
            var worn = LvnCostumer.Committed(_entity, axis, _def?.defaults);
            if (string.IsNullOrEmpty(worn) && list.Count > 0 && list[0].value == LvnWardrobe.NoneValue)
                worn = LvnWardrobe.NoneValue; // пусто и снимаемо — текущее «Нет»
            int cur = string.IsNullOrEmpty(worn) ? -1 : list.FindIndex(i => i.value == worn);
            if (cur > 0)
            {
                var it = list[cur];
                list.RemoveAt(cur);
                list.Insert(0, it);
            }
            return list;
        }

        // Part of the player's collection: seen along the way, bought (the
        // wallet remembers across reinstalls), or what they're wearing right now.
        private bool Encountered(string axis, string value) => Encountered(_entity, axis, value);

        private static bool Encountered(string entity, string axis, string value)
        {
            if (LvnWardrobe.IsSeen(entity, axis, value)) return true;
            // ContainsKey здесь НАРОЧНО, в отличие от проверки владения рядом:
            // вопрос не «есть сейчас», а «было когда-либо», и ключ с нулём —
            // законное свидетельство, что вещь у игрока была.
            if (LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(entity, axis, value))) return true;
            return LvnWardrobe.Equipped(entity).TryGetValue(axis, out var worn) && worn == value;
        }

        private LvnWardrobeItem Find(string axis, string value)
        {
            foreach (var it in Items(axis)) if (it.value == value) return it;
            return null;
        }

        // Choice-button skin: the same fill/text/art the story's choices use,
        // so the wardrobe's controls read as the game's own buttons.
        private void SkinButton(Button b, bool accent)
        {
            LvnStyler.Skinned(b,
                accent ? _accent : UiColor.Parse(_ch?.color, LvnTokens.Faint),
                accent ? _accentText : UiColor.Parse(_ch?.text_color, _text),
                _ch?.corner_radius ?? _radius);
            if (!accent && !string.IsNullOrEmpty(_ch?.button_image))
                LvnAsync.Fire(ApplyNineSliceAsync(b, _ch.button_image, _ch.button_slice ?? 0), "ApplyNineSlice");
            else
                b.style.backgroundImage = new StyleBackground(StyleKeyword.None); // an accent tab drops the art
        }

        private async Task ApplyNineSliceAsync(VisualElement el, string url, int slice)
        {
            if (el == null || string.IsNullOrEmpty(url) || _assets == null) return;
            try
            {
                var sprite = await _assets.LoadSpriteAsync(url, CancellationToken.None);
                if (sprite == null) return;
                el.style.backgroundImage = new StyleBackground(sprite);
                el.style.backgroundColor = Color.clear; // the art replaces the flat fill
                if (slice > 0)
                {
                    el.style.unitySliceLeft = slice;
                    el.style.unitySliceRight = slice;
                    el.style.unitySliceTop = slice;
                    el.style.unitySliceBottom = slice;
                }
            }
            catch { /* missing art keeps the flat look */ }
        }
    }
}
