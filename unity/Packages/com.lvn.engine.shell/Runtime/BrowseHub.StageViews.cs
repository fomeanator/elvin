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

        /// <summary>Вид облика: своя заливка снимается, задник — рамка со своей
        /// серединой, полоса не шире телефона.</summary>
        private void DressView(VisualElement view)
        {
            if (view == null) return;
            view.style.backgroundColor = Color.clear;
            LvnChrome.PhoneColumn(view);
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
