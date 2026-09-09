using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// СПИСОК НОВЕЛЛ И ДЕТАЛЬ — В ОБЛИКЕ «СЦЕНА».
    ///
    /// <para>Главная, магазин, гардероб и профиль давно переодеты, а эти два
    /// вида оставались в прежней теме: тёмная плашка во весь экран, светлая
    /// кнопка «Играть», список глав строками. Переход туда читался как выход из
    /// игры в другое приложение («переделать полностью, сделать отдельной
    /// сущностью меню» — Илья 09.09, TR-63).</para>
    ///
    /// <para>Здесь — первый шаг: оба вида носят облик и не закрывают полотно.
    /// Задник у них становится рамкой облика со своей серединой, шапка «назад»
    /// — плашкой с золотым словом, кнопки — нарисованными кнопками витрины.
    /// Второй шаг (своя комната витрины со своим ходом полотна и местом
    /// героини) живёт в той же задаче.</para>
    /// </summary>
    public sealed partial class BrowseHub
    {
        private bool _stageViewsDressed;

        /// <summary>Одеть список и деталь, если новелла назвала облик. Зовётся
        /// при каждом открытии; работа делается один раз.</summary>
        private void DressStageViews()
        {
            if (_stageViewsDressed || !Staged) return;
            _stageViewsDressed = true;
            DressView(_collectionView);
            DressView(_detailView);
            DressBackBar(_collectionView);
            DressBackBar(_detailView);
            StageifyButton(_detailPlay);
        }

        /// <summary>ПОПАП ДЕТАЛИ: лист поверх того, из чего его позвали, со
        /// скримом и рамкой облика. Раньше деталь была третьим ВИДОМ и
        /// заменяла собой витрину целиком — возврат перерисовывал главную, а
        /// откуда пришёл, было не видно.</summary>
        private void ShowDetailPopup()
        {
            var v = _detailView;
            if (v == null) return;
            DressStageViews();
            if (!_detailPopupArmed)
            {
                _detailPopupArmed = true;
                LvnChrome.Stretch(v);              // поверх всей витрины…
                v.style.backgroundColor = LvnTokens.Veil(0.55f);   // …скрим ловит тап мимо листа
                v.pickingMode = PickingMode.Position;
                v.RegisterCallback<ClickEvent>(e => { if (e.target == v) BackFromDetail(); });
                // Сам лист — полосой не шире телефона, с полями от края экрана.
                LvnAir.Pad(v, LvnTokens.Space3);
                v.style.justifyContent = Justify.Center;
            }
            v.style.display = DisplayStyle.Flex;
            v.BringToFront();
            LvnMotion.FadeIn(v);
        }

        private bool _detailPopupArmed;

        /// <summary>
        /// КАРТОЧКА НОВЕЛЛЫ В ОБЛИКЕ — та же рамка, что у карточки главной.
        ///
        /// <para>Список показывал новеллы строками прежней темы: цветная
        /// плашка, обложка слева, текст справа. Рядом на главной стоит рисованная
        /// карточка с рамкой, плашкой и нарисованной кнопкой — и один и тот же
        /// «Агентство» выглядел в двух местах по-разному.</para>
        ///
        /// <para>Обложка ставится картинкой, а не живым спайном: в списке
        /// карточек много, и каждый постер держал бы свою камеру с текстурой.
        /// Спайн остаётся приметой ГЛАВНОЙ — там он один.</para>
        /// </summary>
        private VisualElement StageTitleCard(LvnTitle t)
        {
            float cw = LvnStageSkin.CardFront.Width, ch = LvnStageSkin.CardFront.Height;
            var c = new VisualElement();
            c.style.width = D(cw); c.style.height = D(ch);
            c.style.flexShrink = 0;
            c.style.marginBottom = D(10f);
            c.style.alignSelf = Align.Center;

            c.Add(StageImage("card-back.png", -D(3f), D(7f),
                             D(LvnStageSkin.CardBack.Width), D(LvnStageSkin.CardBack.Height)));

            var cover = new VisualElement { pickingMode = PickingMode.Ignore };
            At(cover, D(10f), D(19f), D(237f), D(131f));
            cover.style.backgroundColor = _card;
            LvnChrome.Round(cover, D(5f));
            cover.style.overflow = Overflow.Hidden;
            LvnPicture.Fit(cover);
            var art = t.CardArt();
            if (!string.IsNullOrEmpty(art)) LvnPicture.Photo(cover, art, _assets);
            c.Add(cover);

            c.Add(StageImage("card-front.png", 0f, 0f, D(cw), D(ch)));

            bool locked = IsLocked(t);
            var head = LvnStageKit.Plaque(() => locked
                ? LvnWords.Pick("hub.locked", _cfg.locked_text, "Locked")
                : LvnProgress.Current(t) != null
                    ? LvnWords.Pick("hub.continue", _cfg.continue_text, "Continue")
                    : LvnWords.Pick("hub.open", _cfg.open_text, "Open"));
            At(head, D(23f), 0f, D(211f), D(28f));
            c.Add(head);

            var row = ScreenUi.Row(new VisualElement { pickingMode = PickingMode.Ignore });
            At(row, D(17f), D(124f), D(224f), D(14f));
            var bar = LvnStageKit.Progress(out var fill);
            bar.style.flexGrow = 1;
            row.Add(bar);
            var counter = StageLabel(() => ChapterCounter(t), LvnTokens.TextSm, _text);
            counter.style.marginLeft = D(14f);
            counter.style.flexShrink = 0;
            row.Add(counter);
            c.Add(row);
            int total = t.ChaptersOf().Count;
            LvnStageKit.Fill(fill, total > 0
                ? Mathf.Clamp01((float)Mathf.Clamp(LvnProgress.Reached(t), 0, total) / total) : 0f);

            var caption = new VisualElement { pickingMode = PickingMode.Ignore };
            caption.style.position = Position.Absolute;
            caption.style.left = D(12f); caption.style.top = D(162f); caption.style.width = D(234f);
            var name = StageLabel(() => LvnWords.Name("title", t.id, t.name), LvnTokens.TextXl, LvnTokens.Gold);
            name.style.whiteSpace = WhiteSpace.Normal;
            caption.Add(name);
            var sub = StageLabel(() => LvnWords.Name("subtitle", t.id, t.subtitle ?? ""),
                                 LvnTokens.TextXs, LvnTokens.TextDim);
            sub.style.marginTop = D(6f);
            caption.Add(sub);
            c.Add(caption);

            void Open()
            {
                if (locked) { FireLockedHint(LvnWords.Name("title", t.id, t.name), t.locked_hint ?? ""); return; }
                OpenDetail(t, CurrentCollectionOf(t));
            }
            var open = StageButton(() => locked
                ? LvnWords.Pick("hub.locked", _cfg.locked_text, "Locked")
                : LvnWords.Pick("hub.open", _cfg.open_text, "Open"), Open);
            At(open, D(54f), D(213f), D(150f), D(42f));
            c.Add(open);

            c.AddManipulator(new Clickable(Open));
            return c;
        }

        /// <summary>Вид облика: своя заливка снимается, задник — рамка со своей
        /// серединой, полоса не шире телефона.</summary>
        private void DressView(VisualElement view)
        {
            if (view == null) return;
            view.style.backgroundColor = Color.clear;
            // Не translate: FadeIn при показе вида обнуляет его, и вид
            // застывал съехавшим вправо на пол-экрана.
            view.style.maxWidth = LvnPanel.ReferenceWidth;
            view.style.alignSelf = Align.Center;
            LvnStageKit.GlassSheet(view, _cfg?.skin, _assets, LvnTokens.Radius);
        }

        /// <summary>Шапка «назад»: слово стрелки — плашкой облика, заголовок —
        /// золотом шрифтом витрины.</summary>
        private void DressBackBar(VisualElement view)
        {
            if (view == null || view.childCount == 0) return;
            var bar = view[view.childCount - 1] is Button ? null : Bar(view);
            if (bar == null) return;
            foreach (var child in bar.Children())
            {
                if (child is Button b)
                    LvnStyler.Plate(b, UiColor.WithAlpha(LvnTokens.PanelBg, 0.82f),
                                    LvnTokens.Gold, LvnStageKit.D(8f));
                else if (child is Label l)
                {
                    l.style.color = LvnTokens.Gold;
                    LvnFonts.Apply(l, LvnFonts.Display);
                }
            }
        }

        /// <summary>Первая строка вида — та самая шапка «назад».</summary>
        private static VisualElement Bar(VisualElement view)
            => view.childCount > 0 ? view[0] : null;

        /// <summary>Кнопка витрины вместо акцентной: прозрачная, слово золотом
        /// шрифтом витрины — как «ОТКРЫТЬ» на панелях главной.</summary>
        private void StageifyButton(Button b)
        {
            if (b == null) return;
            LvnStyler.Plate(b, UiColor.WithAlpha(LvnTokens.PanelBg, 0.94f),
                            LvnTokens.Gold, LvnStageKit.D(6f));
            LvnStyler.Chosen(b, true, LvnTokens.Gold);   // золотая грань, как у выбранного
            LvnFonts.Apply(b, LvnFonts.Display);
        }
    }
}
