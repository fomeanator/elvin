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
        /// КАРТОЧКА НОВЕЛЛЫ В СПИСКЕ — по макету «Текущие экспедиции» (17.09).
        ///
        /// <para>Список показывал новеллы копией карточки главной: рамка со
        /// скобами, плашка «Открыть» сверху, обложка в окне. Макет списка
        /// другой: светящаяся рамка 360×190, постер во всё окно с тенью к
        /// левому краю, слева колонкой эпоха, название и описание, ниже ход
        /// («Глава 5/12» с полосой или «Пройдено»), а «Открыть» — плашка
        /// внизу по центру, вылезающая за рамку на 7.</para>
        ///
        /// <para>Обложка ставится картинкой, а не живым спайном: в списке
        /// карточек много, и каждый постер держал бы свою камеру с текстурой.
        /// Спайн остаётся приметой ГЛАВНОЙ — там он один.</para>
        /// </summary>
        private VisualElement StageTitleCard(LvnTitle t)
        {
            var g = LvnStageSkin.Glow;
            var c = new VisualElement { name = "stage-title-card" };
            c.style.width = D(g.Width); c.style.height = D(g.Height);
            c.style.flexShrink = 0;
            c.style.marginBottom = D(24f);
            c.style.alignSelf = Align.Center;
            LvnStageKit.Glow(c, _cfg.skin, _assets, ornamentH: 154f);

            // Окно постера — внутри рамки на 6; постер темнеет к левому краю,
            // под текст (так нарисован макет), кромка окна — поверх постера.
            var win = new VisualElement { name = "stage-card-window", pickingMode = PickingMode.Ignore };
            At(win, D(6f), D(6f), D(LvnStageSkin.Window.Width), D(LvnStageSkin.Window.Height));
            win.style.overflow = Overflow.Hidden;
            LvnChrome.Round(win, D(5f));
            LvnPicture.Fit(win);
            var art = t.CardArt();
            if (!string.IsNullOrEmpty(art)) LvnPicture.Photo(win, art, _assets);
            var fade = new VisualElement { pickingMode = PickingMode.Ignore };
            LvnChrome.Stretch(fade);
            fade.style.width = Length.Percent(72f);   // тень — под текст, правая треть постера чистая
            fade.style.backgroundImage = LvnBackdrop.Horizontal(
                UiColor.WithAlpha(LvnStageKit.Ink.Bg, 0.94f), UiColor.WithAlpha(LvnStageKit.Ink.Bg, 0f), smooth: true);
            win.Add(fade);
            c.Add(win);
            LvnStageKit.Window(c, _cfg.skin, _assets);

            // Эпоха, название, описание — колонкой слева.
            var words = new VisualElement { name = "stage-card-words", pickingMode = PickingMode.Ignore };
            At(words, D(17f), D(16f), D(196f), D(117f));
            words.style.overflow = Overflow.Hidden;
            // Эпоха — только если автор её дал: подстановка имени вместо пустого
            // подзаголовка показывала бы игроку id новеллы.
            var era = LvnStageKit.Line(() => LvnWords.Name("subtitle", t.id, t.subtitle ?? ""), 13f, LvnStageKit.Ink.Sand);
            era.name = "stage-card-era";
            if (string.IsNullOrEmpty(t.subtitle)) era.style.display = DisplayStyle.None;
            words.Add(era);
            var name = LvnStageKit.Para(() => LvnWords.Name("title", t.id, t.name).ToUpperInvariant(),
                                        17f, LvnStageKit.Ink.Gold, medium: true);
            name.name = "stage-card-name";
            name.style.marginTop = D(8f);
            name.style.maxHeight = D(42f);
            words.Add(name);
            var desc = LvnStageKit.Para(() => t.card?.description ?? "", 9f, LvnStageKit.Ink.Body);
            desc.style.marginTop = D(6f);
            desc.style.maxHeight = D(43f);   // четыре строки, как в макете; пятая не заглядывает
            desc.style.overflow = Overflow.Hidden;
            words.Add(desc);
            c.Add(words);

            // Ход: полоса и «Глава 5/12», у пройденной — галочка и «Пройдено».
            var course = LvnStageKit.Course(t, _cfg.skin, _assets, textFirst: false);
            At(course, D(17f), D(132f), D(160f), D(27f));
            c.Add(course);

            bool locked = IsLocked(t);
            void Open()
            {
                if (locked) { FireLockedHint(LvnWords.Name("title", t.id, t.name), t.locked_hint ?? ""); return; }
                OpenDetail(t, CurrentCollectionOf(t));
            }
            // «Открыть» — плашка макета внизу по центру, на 7 ниже рамки.
            var open = LvnStageKit.Plate(_cfg.skin, _assets, LvnStageSkin.BtnOpen, "btn-open.png",
                () => locked ? LvnWords.Pick("hub.locked", _cfg.locked_text, "Locked")
                             : LvnWords.Pick("hub.open", _cfg.open_text, "Open"),
                12.8f, Open, name: "stage-card-open");
            open.style.position = Position.Absolute;
            open.style.left = D(107f); open.style.top = D(157f);
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
