using System;
using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// КОМНАТА «СЮЖЕТ РЕАЛЬНОСТИ» — список сообщений (макет 17.09).
    ///
    /// <para>Панель на главной обещала сообщения, а вела в библиотеку. Здесь
    /// сообщения по порядку подборки: постер в окне, состояние строкой
    /// («Прочитано · дата», «Новое сообщение!», «Заблокировано»), название,
    /// справа плашка с письмом или замком. Закрытое сообщение приглушено и
    /// вместо постера показывает «?». Снизу — фильтр «Показывать все /
    /// Только новые».</para>
    ///
    /// <para>Что показывать и что с каждым сообщением — знает витрина
    /// (<see cref="BrowseHub.RealityTitles"/>, <see cref="BrowseHub.MarkOf"/>,
    /// <see cref="BrowseHub.OpenTitle"/>): замок и ход считаются в одном месте
    /// на все списки. Комната отвечает за облик и фильтр.</para>
    ///
    /// <para>Это модаль: фон свой (тьма над полотном), «назад» — стрелка в
    /// шапке или тап по ленте. Открытие сообщения кладёт деталь поверх.</para>
    /// </summary>
    public sealed class RealityScreen : LvnOverlayScreen, ILvnContentAware
    {
        private readonly ILvnAssets _assets;
        private readonly VisualElement _top, _filters;
        private readonly ScrollView _list;
        private BrowseConfig _cfg;
        private string _skin;
        private bool _onlyNew;

        /// <summary>Сообщения по порядку; ставит оболочка из витрины.</summary>
        public Func<IReadOnlyList<LvnTitle>> Titles;
        /// <summary>Состояние сообщения; ставит оболочка из витрины.</summary>
        public Func<LvnTitle, LvnTitleMark> MarkOf;
        /// <summary>Открыть сообщение (деталь или подсказка замка).</summary>
        public Action<LvnTitle> Open;

        /// <summary>Фильтр «только новые» — читают тесты и стенд.</summary>
        public bool OnlyNew => _onlyNew;

        public RealityScreen(ILvnAssets assets)
        {
            _assets = assets;
            style.backgroundColor = UiColor.WithAlpha(LvnStageKit.Ink.Bg, 0.78f);
            pickingMode = PickingMode.Position;

            _top = new VisualElement { name = "reality-top" };
            _top.style.flexShrink = 0;
            _top.style.width = Length.Percent(100f);
            _top.style.maxWidth = LvnPanel.ReferenceWidth;
            _top.style.alignSelf = Align.Center;
            Add(_top);

            _list = LvnScroll.Vertical(showScroller: true);
            _list.name = "reality-list";
            _list.style.flexGrow = 1;
            _list.contentContainer.style.alignItems = Align.Center;
            _list.style.width = Length.Percent(100f);
            _list.style.maxWidth = LvnPanel.ReferenceWidth;
            _list.style.alignSelf = Align.Center;
            Slim(_list);
            Add(_list);

            _filters = ScreenUi.Row(new VisualElement { name = "reality-filters" });
            _filters.style.flexShrink = 0;
            _filters.style.justifyContent = Justify.Center;
            _filters.style.width = Length.Percent(100f);
            _filters.style.maxWidth = LvnPanel.ReferenceWidth;
            _filters.style.alignSelf = Align.Center;
            Add(_filters);

            LvnEdges.Follow(this, _ => Edges());
        }

        public void SetContent(LvnManifest manifest)
        {
            _cfg = manifest?.ui?.browse;
            LvnStageKit.TakeSkin(manifest, ref _skin, () => { });
            Rebuild();
        }

        protected override void OnOpening() => Rebuild();

        private static float D(float dp) => LvnStageKit.D(dp);

        /// <summary>Края: шапка под шапкой оболочки, фильтр над лентой меню.</summary>
        private void Edges()
        {
            _top.style.paddingTop = LvnEdges.Top(this) + D(LvnStageSkin.Sheet.Top + 12f);
            _filters.style.paddingBottom = LvnStageKit.BottomAboveBar(this, LvnStageSkin.Sheet.Bottom);
        }

        public override void Rebuild()
        {
            if (_list == null) return;
            if (!string.IsNullOrEmpty(_skin) && this.Q(name: "stage-scrim-top") == null) LvnStageKit.Scrims(this);
            Edges();

            _top.Clear();
            LvnAir.PadX(_top, D(LvnStageSkin.Sheet.Side));
            _top.Add(LvnStageKit.Header(_skin, _assets,
                () => LvnWords.Pick("hub.news", _cfg?.news_title, "News"),
                () => LvnWords.Of("hub.reality_hint", "Choose a reality story"),
                Cancel));

            _list.Clear();
            _list.style.paddingTop = D(24f);
            _list.style.paddingBottom = D(16f);
            int shown = 0;
            var all = Titles?.Invoke();
            if (all != null)
                foreach (var t in all)
                {
                    if (t == null) continue;
                    var mark = MarkOf?.Invoke(t) ?? LvnTitleMark.New;
                    if (_onlyNew && mark != LvnTitleMark.New) continue;
                    _list.Add(Row(t, mark));
                    shown++;
                }
            if (shown == 0)
            {
                var empty = LvnStageKit.Para(
                    () => LvnWords.Pick("hub.news_empty", _cfg?.news_empty_text, "No new messages").ToUpperInvariant(),
                    14f, LvnStageKit.Ink.Sand);
                empty.name = "reality-empty";
                empty.style.unityTextAlign = TextAnchor.MiddleCenter;
                empty.style.marginTop = D(40f);
                _list.Add(empty);
            }

            _filters.Clear();
            _filters.style.paddingTop = D(10f);
            _filters.Add(Filter("reality-filter-all", () => LvnWords.Of("hub.filter_all", "Show all"), !_onlyNew, () => Toggle(false)));
            var only = Filter("reality-filter-new", () => LvnWords.Of("hub.filter_new", "New only"), _onlyNew, () => Toggle(true));
            only.style.marginLeft = D(12f);
            _filters.Add(only);
        }

        private void Toggle(bool onlyNew)
        {
            if (_onlyNew == onlyNew) return;
            _onlyNew = onlyNew;
            Rebuild();
        }

        /// <summary>Кнопка фильтра: подложка тона витрины, выбранная — с белой
        /// кромкой и светлым словом, остальные — бирюзой.</summary>
        private VisualElement Filter(string name, Func<string> text, bool chosen, Action onTap)
        {
            var b = new VisualElement { name = name };
            b.style.width = D(156f); b.style.height = D(40f);
            b.style.flexShrink = 0;
            b.style.justifyContent = Justify.Center;
            b.style.alignItems = Align.Center;
            b.style.backgroundColor = LvnStageKit.Ink.Plate;
            LvnChrome.Frame(b, D(4f), chosen ? Color.white : LvnStageKit.Ink.Edge, D(2f));
            b.Add(LvnStageKit.Text(() => (text() ?? string.Empty).ToUpperInvariant(), D(12f),
                                   chosen ? LvnStageKit.Ink.Ice : LvnStageKit.Ink.Cyan, medium: true));
            b.AddManipulator(new Clickable(onTap));
            LvnMotion.Tappable(b);
            return b;
        }

        /// <summary>РЯД СООБЩЕНИЯ: рамка 360×78, окно постера слева, состояние и
        /// название, плашка с письмом справа.</summary>
        private VisualElement Row(LvnTitle t, LvnTitleMark mark)
        {
            bool locked = mark == LvnTitleMark.Locked;
            var r = new VisualElement { name = "reality-row" };
            r.style.width = D(360f); r.style.height = D(78f);
            r.style.flexShrink = 0;
            r.style.marginBottom = D(16f);
            if (locked) r.style.opacity = 0.8f;
            LvnStageKit.Glow(r, _skin, _assets, ornamentH: 82f);

            // Окно постера: 167:94, вписано в 66 по высоте.
            var win = new VisualElement { name = "reality-row-window", pickingMode = PickingMode.Ignore };
            LvnStageKit.At(win, D(6f), D(6f), D(117f), D(66f));
            win.style.overflow = Overflow.Hidden;
            LvnChrome.Round(win, D(5f));
            var poster = new VisualElement { pickingMode = PickingMode.Ignore };
            LvnChrome.Stretch(poster);
            LvnPicture.Fit(poster);
            poster.style.opacity = locked ? 0.5f : 0.69f;
            var art = t.CardArt();
            if (!string.IsNullOrEmpty(art)) LvnPicture.Photo(poster, art, _assets);
            win.Add(poster);
            if (locked)
            {
                var q = LvnStageKit.Art(LvnStageKit.SkinUrl(_skin, "glyph-question.png"), _assets,
                                        0f, 0f, D(26f), D(44f), bleed: 0f);
                q.name = "reality-row-locked";
                q.style.left = Length.Percent(50f); q.style.top = Length.Percent(50f);
                q.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
                win.Add(q);
            }
            r.Add(win);
            LvnStageKit.Window(r, _skin, _assets);

            // Слова: состояние · дата, ниже название.
            var words = new VisualElement { name = "reality-row-words", pickingMode = PickingMode.Ignore };
            LvnStageKit.At(words, D(134f), D(11f), D(158f), D(60f));
            if (locked) words.style.opacity = 0.5f;
            var state = ScreenUi.Row(new VisualElement { pickingMode = PickingMode.Ignore });
            state.style.alignItems = Align.Center;
            var stateColor = mark == LvnTitleMark.Done ? LvnStageKit.Ink.Teal
                : mark == LvnTitleMark.New ? LvnStageKit.Ink.Grass : LvnStageKit.Ink.Grey;
            var stateWord = LvnStageKit.Line(() => (mark == LvnTitleMark.Done ? LvnWords.Of("hub.msg_read", "Read")
                : mark == LvnTitleMark.New ? LvnWords.Of("hub.msg_new", "New message!")
                : LvnWords.Of("hub.msg_locked", "Locked")).ToUpperInvariant(), 12f, stateColor);
            stateWord.name = "reality-row-state";
            state.Add(stateWord);
            if (!string.IsNullOrEmpty(t.date))
            {
                var dot = new VisualElement { pickingMode = PickingMode.Ignore };
                dot.style.width = D(3f); dot.style.height = D(3f);
                LvnAir.MarginX(dot, D(4f));
                dot.style.backgroundColor = UiColor.WithAlpha(Color.white, 0.5f);
                LvnChrome.Round(dot, D(2f));
                state.Add(dot);
                state.Add(LvnStageKit.Line(() => t.date, 10f, LvnStageKit.Ink.Mint));
            }
            words.Add(state);
            var nameColor = mark == LvnTitleMark.Done ? LvnStageKit.Ink.Ochre
                : mark == LvnTitleMark.New ? LvnStageKit.Ink.Gold : LvnStageKit.Ink.Cyan;
            var name = LvnStageKit.Para(() => LvnWords.Name("title", t.id, t.name).ToUpperInvariant(), 14f, nameColor, medium: true);
            name.name = "reality-row-name";
            name.style.marginTop = D(6f);
            name.style.maxHeight = D(36f);
            name.style.overflow = Overflow.Hidden;
            words.Add(name);
            r.Add(words);

            // Плашка справа: письмо прочитанное / новое, замок у закрытого.
            string icon = mark == LvnTitleMark.Done ? "icon-mail-read.png"
                : mark == LvnTitleMark.New ? "icon-mail-new.png" : "icon-lock.png";
            var btn = LvnStageKit.IconPlate(_skin, _assets, LvnStageSkin.BtnRow, "btn-row.png", icon, 24f,
                                            () => Open?.Invoke(t), "reality-row-open");
            btn.style.position = Position.Absolute;
            btn.style.right = -D(5.4f);
            btn.style.top = Length.Percent(50f);
            btn.style.translate = new Translate(0f, Length.Percent(-50f));
            r.Add(btn);

            r.AddManipulator(new Clickable(() => Open?.Invoke(t)));
            return r;
        }

        /// <summary>Полоса прокрутки макета: 7 в ширину, без стрелок, бегунок
        /// бирюзой на тёмной дорожке у правого края.</summary>
        private static void Slim(ScrollView sv)
        {
            var sc = sv.verticalScroller;
            if (sc == null) return;
            sc.style.width = D(7f);
            LvnAir.MarginY(sc, D(8f));
            sc.lowButton.style.display = DisplayStyle.None;
            sc.highButton.style.display = DisplayStyle.None;
            var tracker = sc.Q(className: "unity-base-slider__tracker");
            if (tracker != null)
            {
                tracker.style.backgroundColor = UiColor.WithAlpha(LvnStageKit.Ink.Edge, 0.35f);
                tracker.style.width = D(7f); tracker.style.left = 0; tracker.style.right = 0;
                LvnChrome.Frame(tracker, D(3.5f));
            }
            var dragger = sc.Q(className: "unity-base-slider__dragger");
            if (dragger != null)
            {
                dragger.style.backgroundColor = LvnStageKit.Ink.Cyan;
                dragger.style.width = D(7f); dragger.style.left = 0;
                LvnChrome.Frame(dragger, D(3.5f));
            }
        }
    }
}
