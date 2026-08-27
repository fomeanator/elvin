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
    public sealed class WardrobeSheet : VisualElement
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

        // Русские подписи известных эмоций articy-палитры; незнакомая — как есть.
        private static readonly Dictionary<string, string> EmotionRu = new Dictionary<string, string>
        {
            ["idle"] = "Спокойно", ["medium"] = "Нейтрально", ["happy"] = "Радость",
            ["sad"] = "Грусть", ["anger"] = "Злость", ["flirt"] = "Флирт",
            ["delight"] = "Восторг", ["surprised"] = "Удивление", ["fear"] = "Страх",
            ["boredom"] = "Скука", ["discontent"] = "Недовольство", ["dreamy"] = "Мечтательно",
            ["horny"] = "Страсть", ["offence"] = "Обида", ["sarcasm"] = "Сарказм",
            ["shame"] = "Смущение", ["sleep"] = "Сон", ["smirk"] = "Ухмылка",
            ["tears"] = "Слёзы", ["thoughtfulness"] = "Задумчивость",
        };

        /// <summary>Меню-режим: пилюли кошелька прячутся — валюты уже несёт
        /// единый навбар, дубль над плашкой только шумит.</summary>
        public bool HideBalances;

        /// <summary>Сюжетный показ (true, по умолчанию) метит весь предложенный
        /// каталог «встреченным» — он вошёл в путь игрока. Меню-магазин скинов
        /// ставит false: там ВЕСЬ каталог виден как витрина, но коллекцию
        /// игрового гардероба листание витрины раскрывать не должно.</summary>
        public bool MarkSeenOnShow = true;

        private TaskCompletionSource<bool> _tcs;
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

            _title = new Label(_cfg.title ?? "Wardrobe");
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
            var peekLabel = new Label(_cfg.peek_text ?? "Во весь рост");
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
            Smooth(_emoThumb, 130, "top", "height");
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

            _cancel = new Button(Cancel) { text = _cfg.cancel_text ?? "Отменить" };
            _cancel.style.fontSize = 28;
            _cancel.style.flexGrow = 1;
            _cancel.style.flexBasis = 0;
            _cancel.style.marginRight = 10;
            _cancel.style.paddingTop = 14;
            _cancel.style.paddingBottom = 14;
            SkinButton(_cancel, false);
            actions.Add(_cancel);

            _confirm = new Button(() => _ = ConfirmAsync());
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

        /// <summary>Подпись кнопки подтверждения. <paramref name="currency"/> —
        /// валюта цены: её значок встаёт справа от числа вместо служебного имени
        /// («Купить: 20 crystals» игроку ничего не говорит).</summary>
        private void SetConfirmText(string text, string currency = null)
        {
            if (_confirmLabel == null) { _confirm.text = text; return; }
            _confirmLabel.text = text;
            if (_confirmCoin != null) { _confirmCoin.RemoveFromHierarchy(); _confirmCoin = null; }
            if (string.IsNullOrEmpty(currency)) return;
            _confirmCoin = LvnIcons.MakeCurrency(currency, 26f);
            _confirmCoin.style.marginLeft = 8;
            _confirmRow.Add(_confirmCoin);
        }

        private string ConfirmText => _confirmLabel != null ? _confirmLabel.text : _confirm.text;

        public void SetManifest(LvnManifest manifest) => _manifest = manifest;

        private VisualElement _rosterRow;
        private List<(string id, string name)> _roster;

        /// <summary>Give the sheet a character roster (menu/hub mode). Null or a
        /// single entry hides the pills. Call before ShowAsync — cleared state
        /// persists on the shared instance otherwise.</summary>
        public void SetRoster(List<(string id, string name)> roster) => _roster = roster;

        private void RebuildRoster()
        {
            if (_rosterRow == null) return;
            _rosterRow.Clear();
            int shown = 0;
            if (_roster != null && _roster.Count > 1)
            {
                foreach (var (id, name) in _roster)
                {
                    if (OnlySeen && id != _entity && !HasAnyCollected(id)) continue;
                    var pid = id;
                    var b = new Button(() => SwitchTo(pid)) { text = name };
                    b.style.height = 40;
                    b.style.marginLeft = 0; b.style.marginRight = 0; b.style.marginBottom = 8;
                    b.style.paddingLeft = 14; b.style.paddingRight = 14;
                    b.style.fontSize = 19;
                    bool active = pid == _entity;
                    SkinButton(b, active);
                    LvnChrome.Border(b, active ? _accent : new Color(1f, 1f, 1f, 0.15f), 2f);
                    _rosterRow.Add(b);
                    shown++;
                }
            }
            _rosterRow.style.display = shown > 1 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void SwitchTo(string id)
        {
            if (string.IsNullOrEmpty(id) || id == _entity) return;
            var from = _entity;
            LvnWardrobe.ClearPreview(from); // the outgoing look blends back
            OnCharacterPicked?.Invoke(from, id);
            BuildFor(id);
            RefreshBalances();
        }

        // Does this entity have anything to show in collection mode? Mirrors
        // Items()' Encountered rule without switching the sheet to it.
        private bool HasAnyCollected(string id)
        {
            if (_manifest?.sprites == null || !_manifest.sprites.TryGetValue(id, out var d)
                || d?.wardrobe == null) return false;
            foreach (var kv in d.wardrobe)
                if (kv.Value?.items != null)
                    foreach (var it in kv.Value.items)
                        if (it != null && !string.IsNullOrEmpty(it.value) && Encountered(id, kv.Key, it.value))
                            return true;
            return false;
        }

        /// <summary>Open the sheet for a character; resolves when the player
        /// confirms or collapses it. The story op awaits this.</summary>
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
            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetResult(false));
            try { await _tcs.Task; }
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
            _tcs?.TrySetResult(false);
        }

        private void Cancel() => _tcs?.TrySetResult(false);
        // Кошелёк сменился (покупка/начисление) — бейджи цен на карточках
        // обязаны пересчитаться: купленный скин тут же теряет ценник.
        private void OnWalletChanged() { RefreshBalances(); RebuildStrip(); RefreshConfirm(); }

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
                LvnWallet.Balances.TryGetValue(cur, out var amount);
                var pill = new VisualElement();
                pill.style.flexDirection = FlexDirection.Row;
                pill.style.alignItems = Align.Center;
                pill.style.marginRight = 8;
                pill.style.paddingLeft = 12; pill.style.paddingRight = 6;
                pill.style.paddingTop = 5; pill.style.paddingBottom = 5;
                pill.style.backgroundColor = UiColor.Parse(_cfg.panel_color, new Color(0.078f, 0.078f, 0.10f, 0.97f));
                LvnChrome.Round(pill, 16f);

                string iconUrl = _cfg.currency_icons != null
                                 && _cfg.currency_icons.TryGetValue(cur, out var u) ? u : null;
                if (!string.IsNullOrEmpty(iconUrl))
                {
                    var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                    icon.style.width = 26; icon.style.height = 26; icon.style.marginRight = 6;
                    icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                    icon.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                    pill.Add(icon);
                    LvnAsync.Fire(ScreenUi.AssignBgAsync(icon, iconUrl, _assets), "AssignBg");
                }
                else
                {
                    // Своей картинки у валюты нет — берём тот же вектор, которым
                    // кошелёк подписан в строке состояния. Раньше здесь вместо
                    // значка стояло служебное имя валюты («13 060 crystals»):
                    // единственное место в оболочке, где игроку показывали её
                    // внутренний идентификатор.
                    var icon = LvnIcons.MakeCurrency(cur, 24f);
                    icon.style.marginRight = 6;
                    pill.Add(icon);
                }
                var label = new Label(amount.ToString("N0"));
                label.style.color = _text;
                label.style.fontSize = 22;
                pill.Add(label);

                if (OpenStore != null)
                {
                    var plus = new Button(() => _ = OpenStore()) { text = "+" };
                    plus.style.fontSize = 22;
                    plus.style.marginLeft = 8;
                    plus.style.paddingLeft = 10; plus.style.paddingRight = 10;
                    plus.style.paddingTop = 1; plus.style.paddingBottom = 1;
                    plus.style.color = _accentText;
                    plus.style.backgroundColor = _accent;
                    LvnChrome.Round(plus, 12f);
                    pill.Add(plus);
                }
                _balances.Add(pill);
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
            _tabs.Clear();
            _tab = null;
            _title.text = _cfg.title ?? "Wardrobe";

            RebuildRoster();
            if (_def?.wardrobe == null || _def.wardrobe.Count == 0)
            {
                _itemName.text = _cfg.empty_text ?? "The wardrobe is empty";
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
                Smooth(b, 180, "background-color", "border-top-color",
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
                    var icon = IsHairAxis(axis) ? LvnIcon.Crown : LvnIcon.Wardrobe;
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
                Smooth(lbl, 180, "color");
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
                Smooth(all, 180, "background-color", "border-top-color",
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
                var lblA = new Label("Моё") { pickingMode = PickingMode.Ignore };
                lblA.name = "ax-label";
                lblA.style.fontSize = 22;
                lblA.style.whiteSpace = WhiteSpace.NoWrap;
                lblA.style.color = _text;
                Smooth(lblA, 180, "color");
                all.Add(lblA);
                _tabs.Insert(0, all); // «Моё» — первым (Илья 28.08)
            }

            // The hero must OPEN the sheet already dressed from THIS sheet: an
            // axis whose worn value isn't among the scene's items puts on its
            // first OWNED item right away, for every axis — not just the active
            // tab. Otherwise she stands in last chapter's (possibly retired)
            // outfit until the player taps that tab, and the swap lands as a jump.
            //
            // Это ОДЕВАНИЕ, а не примерка, и потому три условия. Надетым
            // считается и дефолт каталога — иначе у игрока, который ещё ничего
            // не выбирал, «надето» пусто, и лист переодевал героиню при каждом
            // открытии. Берётся первый предмет, которым игрок ВЛАДЕЕТ: молча
            // надевать неоплаченное нельзя. И пишется в надетое, а не в
            // превью — превью означает «есть что подтвердить», из-за чего
            // «Выбрать» и «Отменить» горели всегда, хотя игрок ничего не
            // трогал (живой скрин 27.08; в APK, где героиня уже одета из
            // листа, они честно гасли).
            foreach (var kv in _def.wardrobe)
            {
                var axis = kv.Key;
                var items = Items(axis);
                if (items.Count == 0) continue;
                LvnWardrobe.Previewed(_entity).TryGetValue(axis, out var worn);
                if (worn == null) LvnWardrobe.Equipped(_entity).TryGetValue(axis, out worn);
                if (worn == null && _def.defaults != null) _def.defaults.TryGetValue(axis, out worn);
                // Съёмный слот без дефолта (украшения): «ничего не надето» —
                // это и есть пункт «Нет», а не пробел, который надо заполнить.
                if (worn == null && items.Count > 0 && items[0].value == LvnWardrobe.NoneValue)
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
                Debug.Log($"[lvn-wardrobe] лист одевает {_entity}.{axis}: '{worn ?? "-"}' не из этого листа → '{items[owned].value}'");
                LvnWardrobe.Equip(_entity, axis, items[owned].value);
                if (!string.IsNullOrEmpty(kv.Value?.storyVar))
                    OnEquip?.Invoke(_entity, kv.Value.storyVar, items[owned].value);
            }

            RebuildEmotions();
            // «Моё» — вкладка по умолчанию (Илья 28.08), когда она есть.
            SelectTab(_def.wardrobe.Count > 1 ? AllTab : _tab);
        }

        // Ось про волосы? — зеркало правила сцены (её вариант приватный).
        private static bool IsHairAxis(string axis)
        {
            var key = (axis ?? "").ToLowerInvariant();
            return key.Contains("hair") || key.Contains("причес") || key.Contains("волос");
        }

        // ── плавный UI (Илья 28.08: «всё прыгает — надо чтобы плавно
        // переезжал») ─────────────────────────────────────────────────────────
        // Декларативные transition'ы UITK: после подключения любая смена
        // значения (подсветка выбранного, перекраска пилюли) едет кривой сама.
        private static void Smooth(VisualElement el, int ms, params string[] props)
        {
            var list = new List<StylePropertyName>(props.Length);
            foreach (var p in props) list.Add(new StylePropertyName(p));
            el.style.transitionProperty = list;
            el.style.transitionDuration = new List<TimeValue>
                { new TimeValue(ms, TimeUnit.Millisecond) };
            el.style.transitionTimingFunction = new List<EasingFunction>
                { new EasingFunction(EasingMode.EaseOutCubic) };
        }

        private static readonly string[] CardGlide =
        {
            "opacity", "translate", "border-top-color", "border-right-color",
            "border-bottom-color", "border-left-color",
        };

        // Въезд элемента: лёгкий подъём + проявление, каскадом по позиции —
        // перестройка ленты «переезжает», а не мигает. Transition вешается
        // ВНУТРИ отложки: повешенный сразу, он анимировал бы сам старт в ноль.
        private static void EnterSoft(VisualElement el, int i)
        {
            el.style.opacity = 0f;
            el.style.translate = new Translate(0f, 12f);
            el.schedule.Execute(() =>
            {
                Smooth(el, 240, CardGlide);
                el.style.opacity = 1f;
                el.style.translate = new Translate(0f, 0f);
            }).ExecuteLater(16 + Mathf.Min(i, 10) * 26);
        }

        // ── поднастройки: ось-уточнение внутри таба родителя ─────────────────
        // Слот с subOf (цвет волос → subOf:"hairstyle") своего таба не имеет:
        // он рисуется рядом круглых свотчей под лентой родительского раздела.
        private bool IsSubAxis(string axis) =>
            axis != null && _def?.wardrobe != null
            && _def.wardrobe.TryGetValue(axis, out var s)
            && !string.IsNullOrEmpty(s?.subOf) && _def.wardrobe.ContainsKey(s.subOf);

        private IEnumerable<string> SubAxesOf(string parent)
        {
            if (parent == null || _def?.wardrobe == null) yield break;
            foreach (var kv in _def.wardrobe)
                if (kv.Value?.subOf == parent && IsSubAxis(kv.Key)) yield return kv.Key;
        }

        // Что на оси надето прямо сейчас: превью → надетое → дефакт-дефолт →
        // первый предмет. Один источник правды для шаблонных иконок и подписи.
        private string CurrentValueOf(string axis)
        {
            LvnWardrobe.Previewed(_entity).TryGetValue(axis, out var v);
            if (v == null) LvnWardrobe.Equipped(_entity).TryGetValue(axis, out v);
            if (v == null && _def?.defaults != null) _def.defaults.TryGetValue(axis, out v);
            if (string.IsNullOrEmpty(v) || v == LvnWardrobe.NoneValue)
            {
                var items = Items(axis);
                v = items.Count > 0 ? items[0].value : "";
            }
            return v;
        }

        private string NameOfValue(string axis, string value)
        {
            foreach (var it in Items(axis))
                if (it.value == value) return it.name ?? it.value;
            return value;
        }

        // Подпись под каруселью описывает ВЕСЬ образ раздела — «Рыжая:
        // Голливудские волны»: сначала выбранные поднастройки (цвет волос),
        // затем предмет, который листают стрелки. Раньше тап по свотчу писал
        // туда одно своё имя, и «Рыжая» читалась как то, что сейчас мотается
        // стрелками — а мотались причёски (Илья 26.08).
        private void RefreshLabel()
        {
            if (_itemName == null || _tab == null || _tab == AllTab) return;
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

        // Стрелки листают КАРУСЕЛЬ раздела — на сборной витрине «Моё» листать
        // нечего, и живая кнопка, которая ничего не делает, просто врёт.
        private void RefreshArrows()
        {
            bool on = _tab != null && _tab != AllTab && Items(_tab).Count > 1;
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
            if (_tab != null && _tab != AllTab)
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

        // Перетаскивание ScrollView указателем (Илья 28.08: «надо их скролабл
        // сделать»): штатно UITK тянет список только колесом/тач-жестом.
        // Нажатие на чип отдаёт захват чипу; после порога 8px жест признаётся
        // скроллом — захват перехватываем себе, чип получает CaptureOut и клик
        // не срабатывает (тап без движения работает как раньше).
        private static void MakeDragScrollable(ScrollView sv)
        {
            bool down = false, dragging = false;
            int pid = -1;
            Vector2 startPos = default, startOff = default;
            sv.RegisterCallback<PointerDownEvent>(e =>
            {
                down = true; dragging = false; pid = e.pointerId;
                startPos = e.position; startOff = sv.scrollOffset;
            }, TrickleDown.TrickleDown);
            void EndGesture()
            {
                if (pid != -1 && sv.HasPointerCapture(pid)) sv.ReleasePointer(pid);
                down = false; dragging = false; pid = -1;
            }
            sv.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!down || e.pointerId != pid) return;
                // Кнопка уже отпущена, а PointerUp до нас не дошёл. Это штатно:
                // тап по чипу перестраивает колонку, элемент под курсором
                // исчезает — и событие отпускания уходит вместе с ним. Без
                // этой проверки жест оставался «нажатым» навсегда и список
                // ехал за курсором без нажатия (Илья 26.08).
                if (e.pressedButtons == 0) { EndGesture(); return; }
                var d = (Vector2)e.position - startPos;
                if (!dragging && Mathf.Abs(d.y) > 8f)
                {
                    dragging = true;
                    sv.CapturePointer(pid);
                }
                if (dragging)
                    sv.scrollOffset = new Vector2(startOff.x, startOff.y - d.y);
            });
            // TrickleDown: отпускание должно дойти до нас ДО того, как обработчик
            // чипа пересоберёт колонку и заберёт с собой цель события.
            sv.RegisterCallback<PointerUpEvent>(e =>
            {
                if (e.pointerId == pid) EndGesture();
            }, TrickleDown.TrickleDown);
            // Захват потерян не нами (перестройка, другой элемент) — жест мёртв.
            sv.RegisterCallback<PointerCaptureOutEvent>(_ => { down = false; dragging = false; pid = -1; });
        }

        // Колонка эмоций стоит от низа НАВБАРА до верха плашки — в координатах
        // листа, потому пересчёт на каждый layout: лист живёт на разной высоте
        // в меню и в игре, а safe area у каждого устройства своя.
        private void PlaceEmotions()
        {
            if (_emotions == null || panel == null) return;
            float sheetTop = worldBound.yMin;
            if (float.IsNaN(sheetTop) || sheetTop <= 0f) return;
            float rootH = panel.visualTree.layout.height;
            float safeTop = Screen.height > 0 && !float.IsNaN(rootH)
                ? (Screen.height - Screen.safeArea.yMax) / Screen.height * rootH : 0f;
            float navBottom = safeTop + LvnTopBar.RowH + 10f;
            float gap = Mathf.Max(0f, sheetTop - navBottom - 12f);
            // Отступ от навбара — десятая доля зазора (Илья 26.08: «чуть ниже
            // на 10 процентов»), высота — та же половина зазора плюс 15%.
            float top = navBottom + gap * 0.10f;
            float height = Mathf.Max(120f, gap * 0.575f);
            _emotions.style.top = top - sheetTop;
            _emotions.style.bottom = StyleKeyword.Auto;
            // ПОЛОВИНА зазора (Илья 28.08: «слишком много — сократи в 2 раза»):
            // колонка на всю высоту закрывала куклу; остальные лица скроллятся.
            _emotions.style.maxHeight = height;
            if (_emoBar != null)
            {
                _emoBar.style.top = top - sheetTop;
                _emoBar.style.height = height;
            }
            // Герои — та же полка у левого края: две колонки читаются как пара.
            if (_rosterRow != null)
            {
                _rosterRow.style.top = top - sheetTop;
                _rosterRow.style.maxHeight = height;
            }
            UpdateEmoScrollBar();
        }

        // Бегунок дорожки: длина — доля видимого списка, положение — доля
        // прокрутки. Дорожка прячется целиком, когда лица помещаются разом:
        // индикатор, который нечего индицировать, — просто шум.
        private void UpdateEmoScrollBar()
        {
            if (_emoBar == null || _emoThumb == null || _emotions == null) return;
            float view = _emotions.contentViewport.layout.height;
            float content = _emotions.contentContainer.layout.height;
            bool visible = _emotions.style.display != DisplayStyle.None;
            if (!visible || float.IsNaN(view) || float.IsNaN(content) || content <= view + 1f)
            {
                _emoBar.style.display = DisplayStyle.None;
                return;
            }
            _emoBar.style.display = DisplayStyle.Flex;
            float barH = _emoBar.layout.height;
            if (float.IsNaN(barH) || barH <= 1f) return;
            float thumbH = Mathf.Clamp(barH * (view / content), 26f, barH);
            float p = Mathf.Clamp01(_emotions.scrollOffset.y / Mathf.Max(1f, content - view));
            _emoThumb.style.height = thumbH;
            _emoThumb.style.top = (barH - thumbH) * p;
        }

        // ── баблики эмоций: примерка лица на живую куклу ─────────────────────
        private void RebuildEmotions()
        {
            if (_emotions == null) return;
            _emotions.Clear();
            _emotionAxis = null;
            List<string> vals = null;
            if (_def?.axes != null)
                foreach (var kv in _def.axes)
                {
                    var k = (kv.Key ?? "").ToLowerInvariant();
                    if ((k.Contains("emo") || k.Contains("эмо") || k == "mood" || k == "face")
                        && kv.Value != null && kv.Value.Count > 1)
                    { _emotionAxis = kv.Key; vals = kv.Value; break; }
                }
            // Ось, оформленная гардеробным слотом, — наряд, а не лицо.
            if (_emotionAxis != null && _def.wardrobe != null
                && _def.wardrobe.ContainsKey(_emotionAxis)) _emotionAxis = null;
            _emotions.style.display = _emotionAxis == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (_emotionAxis == null)
            {
                if (_emoBar != null) _emoBar.style.display = DisplayStyle.None;
                return;
            }

            foreach (var v in vals)
            {
                if (string.IsNullOrEmpty(v)) continue;
                var value = v;
                var chip = new Button(() =>
                {
                    LvnWardrobe.Preview(_entity, _emotionAxis, value); // лицо — живьём
                    // reveal:false — подвоз выбранного СДВИГАЛ список из-под
                    // пальца сразу после тапа, читалось как «не применилось,
                    // жми второй раз» (живой репорт 28.08).
                    StyleEmotions(reveal: false);
                }) { text = EmotionRu.TryGetValue(v, out var ru) ? ru : v };
                chip.name = "emo-" + v;
                chip.style.height = 44;
                chip.style.marginBottom = 8;
                chip.style.flexShrink = 0;
                chip.style.paddingLeft = 16; chip.style.paddingRight = 16;
                chip.style.fontSize = 19;
                LvnChrome.Round(chip, 22f);
                Smooth(chip, 180, "background-color", "color");
                _emotions.Add(chip);
            }
            StyleEmotions();
        }

        // reveal — подвезти выбранный чип в кадр: только при перестройке
        // колонки (открытие, смена персонажа), НИКОГДА после тапа.
        private void StyleEmotions(bool reveal = true)
        {
            if (_emotionAxis == null) return;
            LvnWardrobe.Previewed(_entity).TryGetValue(_emotionAxis, out var current);
            if (current == null && _def?.defaults != null)
                _def.defaults.TryGetValue(_emotionAxis, out current);
            foreach (var c in _emotions.contentContainer.Children())
            {
                var b = c as Button;
                if (b == null) continue;
                bool on = b.name == "emo-" + current;
                SkinButton(b, on);
                if (on && reveal) _emotions.schedule.Execute(() =>
                {
                    if (b.panel != null && b.parent == _emotions.contentContainer)
                        _emotions.ScrollTo(b);
                });
            }
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
                LvnWardrobe.Previewed(_entity).TryGetValue(_tab, out var current);
                if (current == null) LvnWardrobe.Equipped(_entity).TryGetValue(_tab, out current);
                if (current == null && _def?.defaults != null) _def.defaults.TryGetValue(_tab, out current);
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
        /// наезд вместо общего плана).</summary>
        public const string AllTab = "__all__";

        private bool IsOwnedIn(string axis, LvnWardrobeItem item) =>
            item == null || item.price <= 0
            || LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(_entity, axis, item.value));

        // Кадр витрины по разделу (цифры Ильи 27.08): причёска и цвет — к
        // голове, украшения — к шее, платья — к корпусу; «Все» — фигура
        // целиком без зума.
        private (float zoom, float anchorY) StripFraming(string axis)
        {
            if (axis == AllTab) return (1.07f, 0.5f); // лёгкий зум (Илья 28.08)
            var k = (axis ?? "").ToLowerInvariant();
            if (IsHairAxis(k)) return (1.60f, 0.35f);
            // Украшения показывают КРОП-ИКОНКИ (вырезаны по содержимому при
            // импорте) — зум витрины им не нужен, Contain даёт ожерелье во
            // всю плитку без мыла.
            if (k.Contains("decor") || k.Contains("jewel") || k.Contains("украш")
                || k.Contains("acc")) return (1f, 0.5f);
            return (1.55f, 0.60f); // платье/наряд
        }

        // animate=false — ЛЕНТА УЖЕ НА ЭКРАНЕ и просто пересобирается после
        // примерки (тап по свотчу цвета, тап по карточке в «Моё»): проигрывать
        // въезд карточек заново значит дёргать неподвижный список под пальцем
        // (Илья 26.08). Въезд принадлежит появлению ленты — смене раздела,
        // персонажа, открытию листа.
        private void RebuildStrip(bool animate = true)
        {
            if (_strip == null) return;
            _strip.Clear();
            _stripCards.Clear();
            if (_tab == AllTab)
            {
                // Сборная витрина: пары (ось, предмет) — тап примеряет в СВОЮ
                // ось; подсветка по надетому каждой оси.
                int shown = 0;
                if (_def?.wardrobe != null)
                    foreach (var kv in _def.wardrobe)
                    {
                        // Поднастройка — не самостоятельный скин: цвет волос
                        // выбирается только внутри «Причёски», и в витрине
                        // покупок ему делать нечего (Илья 26.08).
                        if (IsSubAxis(kv.Key)) continue;
                        foreach (var it in Items(kv.Key))
                        {
                            // Только КУПЛЕННЫЕ платные (Илья 28.08): бесплатная
                            // база есть у всех — витрина «Все» показывает именно
                            // коллекцию покупок.
                            if (it.value == LvnWardrobe.NoneValue || it.price <= 0
                                || !IsOwnedIn(kv.Key, it)) continue;
                            var card = StripCard(kv.Key, -1, it);
                            _strip.Add(card);
                            _stripCards.Add(card);
                            if (animate) EnterSoft(card, shown);
                            shown++;
                        }
                    }
                _strip.style.display = shown > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                _itemName.text = shown > 0 ? "Мои скины" : "Пока пусто — загляни в разделы";
                RebuildSubRow(animate); // спрячет ряд: у «Все» поднастроек нет
                RefreshArrows();
                return;
            }
            var items = Items(_tab);
            _strip.style.display = items.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            for (int i = 0; i < items.Count; i++)
            {
                var card = StripCard(_tab, i, items[i]);
                _strip.Add(card);
                _stripCards.Add(card);
                if (animate) EnterSoft(card, i);
            }
            RebuildSubRow(animate);
            RefreshArrows();
            StyleStrip();
        }

        // Карточка: арт скина во всю плитку, цена бейджем ПРЯМО на арте
        // (у купленных и бесплатных бейджа нет), имя на серой подложке снизу.
        private VisualElement StripCard(string axis, int i, LvnWardrobeItem item)
        {
            bool owned = IsOwnedIn(axis, item);
            // Крупнее (Илья 27.08): плитка подросла, арт занимает почти всю
            // её площадь (~+70%), имя заметно больше (~+50%).
            var card = new VisualElement();
            card.style.width = 150; card.style.height = 208;
            card.style.marginRight = 10;
            card.style.flexShrink = 0;
            // Платина #D1D1D6 (Илья 26.08) вместо прежней тускло-серой заливки:
            // арт скинов тёмный, и светлый задник держит его силуэт.
            card.style.backgroundColor = UiColor.Parse("#D1D1D6", new Color(0.82f, 0.82f, 0.84f));
            LvnChrome.Round(card, _radius);
            card.style.overflow = Overflow.Hidden; // арт и подложка не выходят за скругление

            // ЗУМ ВИТРИНЫ ПО РАЗДЕЛУ (Илья 27.08): причёска кадрируется к
            // голове, украшения — к шее, платье — к корпусу; «Все» — фигура
            // целиком. Элемент больше карточки, карточка клипует излишек.
            var (zoom, ay) = StripFraming(axis);
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.style.position = Position.Absolute;
            art.style.width = Length.Percent(zoom * 100f);
            art.style.height = Length.Percent(zoom * 100f);
            art.style.left = Length.Percent(50f - zoom * 100f * 0.50f); // якорь X в центре окна
            art.style.top = Length.Percent(50f - zoom * 100f * ay);    // якорь Y в центре окна
            art.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            art.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            card.Add(art);
            // Плейсхолдер-вешалка, пока арт едет (Илья 27.08): пустая чёрная
            // плитка читалась как «не грузит». Пункт «Нет» живёт с постоянным
            // глифом «×» — у снятия арта нет по определению.
            bool none = item.value == LvnWardrobe.NoneValue;
            // Тёмный глиф: задник плитки светлый (платина), светлый значок на
            // нём растворялся бы.
            var ph = LvnIcons.Make(none ? LvnIcon.Close : LvnIcon.Wardrobe, 42f,
                new Color(0.18f, 0.18f, 0.22f));
            ph.pickingMode = PickingMode.Ignore;
            ph.style.position = Position.Absolute;
            ph.style.left = Length.Percent(50f);
            ph.style.top = Length.Percent(38f);
            ph.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
            ph.style.opacity = 0.55f;
            card.Add(ph);
            if (!string.IsNullOrEmpty(item.icon))
                // Сильный зум (украшения) на 256px-мини даёт кашу — такой кадр
                // берёт чёткий арт (@2k) сразу.
                LvnAsync.Fire(AssignCardArtAsync(art, ph, ResolveIcon(item.icon), sharp: zoom >= 3f),
                    "WardrobeCard");

            var plate = new VisualElement { pickingMode = PickingMode.Ignore };
            plate.style.position = Position.Absolute;
            plate.style.left = 0; plate.style.right = 0; plate.style.bottom = 0;
            plate.style.backgroundColor = new Color(0.16f, 0.16f, 0.19f, 0.85f);
            plate.style.paddingTop = 6; plate.style.paddingBottom = 8;
            plate.style.paddingLeft = 6; plate.style.paddingRight = 6;
            card.Add(plate);

            var name = new Label(item.name ?? item.value) { pickingMode = PickingMode.Ignore };
            name.style.color = _text;
            name.style.fontSize = 25;
            name.style.unityTextAlign = TextAnchor.MiddleCenter;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            plate.Add(name);

            if (!owned)
            {
                var badge = new Label($"◆ {item.price:N0}") { pickingMode = PickingMode.Ignore };
                badge.style.position = Position.Absolute;
                badge.style.top = 6; badge.style.right = 6;
                badge.style.backgroundColor = new Color(0f, 0f, 0f, 0.62f);
                badge.style.color = LvnTokens.Gold;
                badge.style.fontSize = 19;
                badge.style.unityFontStyleAndWeight = FontStyle.Bold;
                badge.style.paddingLeft = 8; badge.style.paddingRight = 8;
                badge.style.paddingTop = 3; badge.style.paddingBottom = 3;
                LvnChrome.Round(badge, 10f);
                card.Add(badge);
            }

            int at = i;
            if (at < 0)
            {
                // Сборный таб «Все»: тап примеряет предмет в ЕГО ось; подсветка
                // и имя обновляются перестройкой (кэш делает её мгновенной).
                bool worn = IsWornIn(axis, item.value);
                LvnChrome.Border(card, worn ? _accent : new Color(1f, 1f, 1f, 0.12f),
                    worn ? 2.5f : 1.5f);
                var a2 = axis; var v2 = item.value; var n2 = item.name ?? item.value;
                card.RegisterCallback<ClickEvent>(_ =>
                {
                    LvnWardrobe.Preview(_entity, a2, v2);
                    RebuildStrip(animate: false);
                    _itemName.text = n2;
                    RefreshConfirm(); // примерка состоялась — кнопкам ожить
                });
                return card;
            }
            card.RegisterCallback<ClickEvent>(_ =>
            {
                if (_tab == null || _tab == AllTab) return;
                _index[_tab] = at;
                ShowItem(); // примерка + имя в карусели + подсветка — одно состояние
            });
            return card;
        }

        // Носится ли значение на оси прямо сейчас (превью сильнее надетого).
        private bool IsWornIn(string axis, string value)
        {
            LvnWardrobe.Previewed(_entity).TryGetValue(axis, out var cur);
            if (cur == null) LvnWardrobe.Equipped(_entity).TryGetValue(axis, out cur);
            if (cur == null && _def?.defaults != null) _def.defaults.TryGetValue(axis, out cur);
            return cur == value;
        }

        // Арт карточки — МИНИ-ВЕРСИЯ (Илья 27.08: «не тянуть огромные, если
        // юзер даже не тыкнет»): витрина живёт на @mini, полноразмер грузит
        // только примерка на кукле. Пока не доехал (или мини нет и доезжает
        // полный) — стоит плейсхолдер-вешалка.
        private async Task AssignCardArtAsync(VisualElement art, VisualElement ph, string icon,
            bool sharp = false)
        {
            Sprite s = null;
            var mini = sharp ? null : Lvn.Content.DownloadPolicy.MiniVariant(icon);
            string via = "mini";
            try { if (!string.IsNullOrEmpty(mini)) s = await _assets.LoadSpriteAsync(mini, CancellationToken.None); }
            catch (Exception ex) { Debug.LogWarning($"[lvn-card-art] mini {mini}: {ex.Message}"); }
            if (s == null)
            {
                via = "full";
                try { s = await _assets.LoadSpriteAsync(icon, CancellationToken.None); }
                catch (Exception ex) { Debug.LogWarning($"[lvn-card-art] full {icon}: {ex.Message}"); }
            }
            // Полный файловый след тракта витрины: какой url пробовали, чем
            // кончилось — «одни вешалки» разбираются по этому логу, а не
            // догадками (просьба Ильи 27.08).
            if (s == null)
            {
                Debug.LogWarning($"[lvn-card-art] ПУСТО: mini={mini ?? "-"} и full={icon} не дали спрайта");
                return;
            }
            Debug.Log($"[lvn-card-art] ok via {via}: {(via == "mini" ? mini : icon)} ({s.texture?.width}x{s.texture?.height})");
            // НЕ проверять art.panel: мгновенный кэш-хит завершается ДО того,
            // как RebuildStrip добавил карточку в панель, и страж по panel
            // молча выбрасывал арт — «показались, а при возврате на таб
            // пропали» (живой скрин 27.08).
            art.style.backgroundImage = new StyleBackground(s);
            ScreenUi.PinBg(art, s, _assets); // видимый арт LRU не трогает
            ph.style.display = DisplayStyle.None;
        }

        // Подсветка текущего и доводка ленты: выбранная карточка всегда в кадре
        // (стрелки карусели листают — лента едет следом).
        private void StyleStrip()
        {
            int cur = _tab != null && _index.TryGetValue(_tab, out var i) ? i : 0;
            for (int k = 0; k < _stripCards.Count; k++)
            {
                bool on = k == cur;
                LvnChrome.Border(_stripCards[k],
                    on ? _accent : new Color(1f, 1f, 1f, 0.12f), on ? 2.5f : 1.5f);
            }
            if (cur >= 0 && cur < _stripCards.Count)
            {
                var target = _stripCards[cur];
                // Отложенно — после лейаута; к этому моменту ленту могли уже
                // перестроить (смена оси, покупка, переоткрытие листа), и чужая
                // карточка роняет ScrollTo ArgumentException'ом (живой лог).
                _strip.schedule.Execute(() =>
                {
                    if (target.panel == null || target.parent != _strip.contentContainer) return;
                    _strip.ScrollTo(target);
                });
            }
        }

        internal void Step(int dir)
        {
            var items = Items(_tab);
            if (items.Count == 0) return;
            _index[_tab] = ((_index.TryGetValue(_tab, out var i) ? i : 0) + dir + items.Count) % items.Count;
            ShowItem();
        }

        private void ShowItem()
        {
            var items = Items(_tab);
            if (items.Count == 0) return;
            var item = items[Mathf.Clamp(_index.TryGetValue(_tab, out var i) ? i : 0, 0, items.Count - 1)];
            RefreshLabel();
            bool owned = item.price <= 0
                || LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(_entity, _tab, item.value));
            Debug.Log($"[lvn-wardrobe] sheet preview {_entity}.{_tab} = '{item.value}' " +
                      $"(price={item.price} {item.currency ?? "-"}, owned={owned})");
            LvnWardrobe.Preview(_entity, _tab, item.value); // the live actor is the mirror
            StyleStrip(); // лента подсвечивает и довозит выбранную карточку
            RefreshConfirm();
        }

        // The item the carousel is showing on the active tab — the button's
        // subject. BUY and CHOOSE are separate acts (partner's ask): browsing
        // an unowned priced item offers to buy JUST IT (the sheet stays open,
        // so a hairstyle and a jacket buy back-to-back); once owned, the same
        // button turns into the plain "choose" that commits the look.
        private LvnWardrobeItem CurrentItem()
        {
            var items = Items(_tab);
            if (items.Count == 0) return null;
            return items[Mathf.Clamp(_index.TryGetValue(_tab, out var i) ? i : 0, 0, items.Count - 1)];
        }

        private bool IsOwned(LvnWardrobeItem item) =>
            item == null || item.price <= 0
            || LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(_entity, _tab, item.value));

        // Что кнопке предлагать купить: предмет карусели, а если он свой —
        // непринадлежащая примерка из ПОДНАСТРОЕК раздела (цвет волос тоже
        // товар, но своей карусели у него нет).
        private (string axis, LvnWardrobeItem item) PendingBuy()
        {
            var cur = CurrentItem();
            if (cur != null && !IsOwned(cur)) return (_tab, cur);
            if (_tab != null && _tab != AllTab)
                foreach (var sub in SubAxesOf(_tab))
                {
                    LvnWardrobe.Previewed(_entity).TryGetValue(sub, out var v);
                    if (v == null) continue;
                    var it = Find(sub, v);
                    if (it != null && !IsOwnedIn(sub, it)) return (sub, it);
                }
            return (null, null);
        }

        /// <summary>Лист живёт ВКЛАДКОЙ (уйти можно навбаром), а не модалкой.
        /// Тогда «Отменить» — не единственный выход, и его честно гасить,
        /// когда отменять нечего; в сюжетном листе он гаснуть не смеет.</summary>
        public bool TabMode;

        // Есть ли НЕСОХРАНЁННАЯ примерка: превью, отличающееся от надетого, по
        // ГАРДЕРОБНОЙ оси. Лицо примеряется мимо гардероба («Выбрать» его не
        // коммитит) и в счёт не идёт, иначе тап по эмоции оживлял бы кнопки.
        private bool HasPendingLook()
        {
            if (_def?.wardrobe == null) return false;
            foreach (var kv in LvnWardrobe.Previewed(_entity))
            {
                if (!_def.wardrobe.ContainsKey(kv.Key)) continue;
                LvnWardrobe.Equipped(_entity).TryGetValue(kv.Key, out var worn);
                if (worn == null && _def.defaults != null) _def.defaults.TryGetValue(kv.Key, out worn);
                // «Ничего не надето» и примерка пункта «Нет» — одно состояние:
                // подтверждать в нём нечего, и кнопки не должны оживать.
                if (Bare(kv.Value) && Bare(worn)) continue;
                if (kv.Value != worn) return true;
            }
            return false;
        }

        private static bool Bare(string value)
            => string.IsNullOrEmpty(value) || value == LvnWardrobe.NoneValue;

        private void RefreshConfirm()
        {
            var (axis, item) = PendingBuy();
            // Кнопки честны, как стрелки (Илья 26.08): «Выбрать» живёт, пока
            // есть что купить или что применить, «Отменить» — пока есть что
            // отменять. Живая кнопка, которая ничего не сделает, врёт.
            bool pending = HasPendingLook();
            if (!_buying) _confirm.SetEnabled(item != null || pending);
            _cancel?.SetEnabled(pending || !TabMode);
            _confirm.style.opacity = _confirm.enabledSelf ? 1f : 0.4f;
            if (_cancel != null) _cancel.style.opacity = _cancel.enabledSelf ? 1f : 0.4f;

            if (item != null)
            {
                // Слово вместо значка — только если новелла сама его назвала
                // (currency_label). Иначе игрок читал внутренний идентификатор.
                bool named = !string.IsNullOrEmpty(_cfg.currency_label);
                SetConfirmText(named
                        ? $"{_cfg.buy_text ?? "Buy"}:  {item.price:N0} {_cfg.currency_label}"
                        : $"{_cfg.buy_text ?? "Buy"}:  {item.price:N0}",
                    named ? null : item.currency);
                Debug.Log($"[lvn-wardrobe] sheet buy offer {_entity}.{axis}='{item.value}' " +
                          $"{item.price} {item.currency}, have {(LvnWallet.Balances.TryGetValue(item.currency ?? "", out var b) ? b : 0)}");
            }
            else SetConfirmText(_cfg.confirm_text ?? "Choose");
        }

        internal async Task ConfirmAsync()
        {
            if (_buying) return;
            _buying = true;
            var label = ConfirmText;
            _confirm.SetEnabled(false);
            SetConfirmText("…");
            try
            {
                // Re-sync the wallet FIRST: ownership decisions below must run
                // against the server's inventory, not a stale mirror — otherwise
                // an already-bought item could be charged twice.
                await LvnWallet.RefreshAsync();

                var (buyAxis, buyItem) = PendingBuy();
                if (buyItem != null)
                {
                    await BuyCurrentAsync(buyAxis, buyItem);
                    return; // the sheet stays open — buying is not choosing
                }

                // CHOOSE: commit every previewed piece the player owns (or that
                // is free). An unowned priced piece browsed on another tab was
                // never bought — snap it back rather than silently charging.
                var previewed = new Dictionary<string, string>();
                foreach (var kv in LvnWardrobe.Previewed(_entity)) previewed[kv.Key] = kv.Value;
                Debug.Log($"[lvn-wardrobe] sheet CHOOSE: previewed [{string.Join(", ", ToPairs(previewed))}], " +
                          $"inventory [{string.Join(", ", LvnWallet.Inventory.Keys)}]");
                foreach (var kv in previewed)
                {
                    var item = Find(kv.Key, kv.Value);
                    if (item == null)
                    {
                        Debug.LogWarning($"[lvn-wardrobe] previewed {kv.Key}='{kv.Value}' has NO catalog item — skipped");
                        continue;
                    }
                    bool owned = item.price <= 0
                        || LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(_entity, kv.Key, item.value));
                    if (!owned)
                    {
                        LvnWardrobe.Preview(_entity, kv.Key, null); // browsed, not bought
                        continue;
                    }
                    LvnWardrobe.Equip(_entity, kv.Key, kv.Value);
                    // Write the pick back into the novel's story state (nested var) so
                    // its downstream logic reads the choice — only for axes bound to one.
                    string sv = _def?.wardrobe != null && _def.wardrobe.TryGetValue(kv.Key, out var slot)
                        ? slot.storyVar : null;
                    if (!string.IsNullOrEmpty(sv)) OnEquip?.Invoke(_entity, sv, kv.Value);
                }
                Debug.Log($"[lvn-wardrobe] sheet choose DONE — equipped [{string.Join(", ", ToPairs(LvnWardrobe.Equipped(_entity)))}]");
                LvnWardrobe.ClearPreview(_entity); // equips now cover the look
                _tcs?.TrySetResult(true);
            }
            finally
            {
                _buying = false;
                if (_confirm.enabledSelf == false && ConfirmText == "…")
                { _confirm.SetEnabled(true); SetConfirmText(label); }
            }
        }

        // Buy exactly the browsed item; on success the button flips to the
        // plain "choose" (RefreshConfirm sees it owned) and the player keeps
        // shopping — the next tab's piece is one more press away.
        private async Task BuyCurrentAsync(string axis, LvnWardrobeItem item)
        {
            var sku = LvnWardrobe.Sku(_entity, axis, item.value);
            Debug.Log($"[lvn-wardrobe] buying {sku}: {item.price} {item.currency ?? "(null currency!)"}");
            bool ok = await LvnWallet.SpendAsync(item.currency, item.price, "wardrobe", sku);
            Debug.Log($"[lvn-wardrobe] buy {sku} → {(ok ? "OK" : "FAILED")}; " +
                      $"balances now [{string.Join(", ", ToPairs(LvnWallet.Balances))}]");
            LvnAnalytics.Track(ok ? "wardrobe_buy" : "wardrobe_buy_fail",
                ("entity", _entity), ("sku", sku));
            if (ok)
            {
                _buying = false;
                RebuildStrip(); // бейджи «◆» на карточках и свотчах устарели
                RefreshConfirm();
                return;
            }

            // В попапе значок не нарисуешь — но и служебное имя валюты
            // показывать нельзя: без названного currency_label остаётся цена.
            string title = _cfg.insufficient_text ?? "Not enough";
            string msg = string.IsNullOrEmpty(_cfg.currency_label)
                ? $"{title}: {item.price:N0}"
                : $"{title}: {item.price:N0} {_cfg.currency_label}";

            // Offer the store and retry once — same pattern as the chapter/title
            // entry gates. Falls back to just flashing the reason on the button
            // if the host hasn't wired the popup hooks (older shells).
            if (ConfirmTopUp != null && OpenStore != null)
            {
                bool toStore = await ConfirmTopUp(title, msg);
                if (toStore)
                {
                    await OpenStore();
                    await LvnWallet.RefreshAsync();
                    ok = await LvnWallet.SpendAsync(item.currency, item.price, "wardrobe", sku);
                    if (!ok && Alert != null) await Alert(title, msg);
                }
                _buying = false;
                RefreshConfirm();
                return;
            }

            // No popup hooks wired — fall back to flashing the reason on the button.
            SetConfirmText(msg);
            _confirm.schedule.Execute(() =>
            {
                _confirm.SetEnabled(true);
                RefreshConfirm();
            }).ExecuteLater(1800);
        }

        private static IEnumerable<string> ToPairs<T>(IReadOnlyDictionary<string, T> map)
        {
            foreach (var kv in map) yield return $"{kv.Key}={kv.Value}";
        }

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
                    list.Add(new LvnWardrobeItem { value = LvnWardrobe.NoneValue, name = "Нет" });
                foreach (var it in slot.items)
                    if (it != null && !string.IsNullOrEmpty(it.value)
                        && (!OnlySeen || Encountered(axis, it.value)))
                        list.Add(it);
            }
            // ТЕКУЩЕЕ — ПЕРВЫМ (Илья: «по умолчанию должно отображаться
            // первым, а не скакать при входе»): надетое (или дефолт; для
            // съёмного пустого — «Нет») переезжает в голову списка. Порядок
            // стабилен на всю примерку — Equip меняет его только по «Выбрать».
            LvnWardrobe.Equipped(_entity).TryGetValue(axis ?? "", out var worn);
            if (worn == null && _def?.defaults != null && axis != null)
                _def.defaults.TryGetValue(axis, out worn);
            if (worn == null && list.Count > 0 && list[0].value == LvnWardrobe.NoneValue)
                worn = LvnWardrobe.NoneValue; // пусто и снимаемо — текущее «Нет»
            int cur = worn == null ? -1 : list.FindIndex(i => i.value == worn);
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
            b.style.color = accent ? _accentText : UiColor.Parse(_ch?.text_color, _text);
            b.style.backgroundColor = accent
                ? _accent
                : UiColor.Parse(_ch?.color, new Color(1f, 1f, 1f, 0.07f));
            LvnChrome.Round(b, _ch?.corner_radius ?? _radius);
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
