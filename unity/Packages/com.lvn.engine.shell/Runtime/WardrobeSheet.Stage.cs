using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ОБЛИК «СЦЕНА» ДЛЯ ГАРДЕРОБА. Главная и магазин рисуются обликом
    /// <c>ui.browse.skin</c> — рамки-арт, тёмные плашки, золотые слова, шрифт
    /// витрины, — а гардероб стоял в общих токенах темы: свои заливки, свои
    /// кнопки, свой задник («гардероб тоже в стиле сделать, а то он не
    /// стилизован — кнопки, задник» — Илья 08.09).
    ///
    /// <para>Здесь те же приёмы, что у столбика магазина (PackShopScreen.Stage):
    /// тёмная плашка тона панели под ряд слов и под плитку, золото у
    /// выбранного, приглушённое у прочих, шрифт витрины. Рамка-арт
    /// (card-back.png) достаётся заднику вкладки — WardrobeTabScreen; плитки
    /// героев и слова эмоций для рамки с угловыми скобами малы.</para>
    ///
    /// <para>Без облика всё как было: каждое правило ниже включается только
    /// при <see cref="StageDressed"/>, а тон и акцент новеллы без облика
    /// по-прежнему читаются из её же настроек.</para>
    /// </summary>
    public sealed partial class WardrobeSheet
    {
        private static float D(float dp) => LvnStageKit.D(dp);

        /// <summary>Облик «сцена» назначен новеллой (тот же признак, что у
        /// главной и магазина).</summary>
        internal bool StageDressed => !string.IsNullOrEmpty(_manifest?.ui?.browse?.skin);

        /// <summary>Чем подсвечен выбранный: золото облика, иначе акцент листа.</summary>
        private Color ChosenInk => StageDressed ? LvnTokens.Gold : _accent;

        /// <summary>Плашка облика: тёмная заливка тона панели без рамки и
        /// без арт-фона, скруглённая. Выбранному — плотнее.</summary>
        private static void StagePlaque(VisualElement el, bool dense = false, float radiusDp = 8f, float alpha = 0.82f)
        {
            el.style.backgroundColor = UiColor.WithAlpha(LvnTokens.PanelBg, dense ? 0.94f : alpha);
            el.style.backgroundImage = new StyleBackground(StyleKeyword.None);
            LvnChrome.ClearBorder(el);
            LvnChrome.Round(el, D(radiusDp));
        }

        /// <summary>Слово облика: золото у выбранного, приглушённое у прочих,
        /// шрифт витрины.</summary>
        private static void StageWord(TextElement l, bool on, float size)
        {
            l.style.color = on ? LvnTokens.Gold : LvnTokens.TextDim;
            l.style.fontSize = size;
            l.style.unityFontStyleAndWeight = FontStyle.Normal;
            LvnFonts.Apply(l, LvnFonts.Display);
        }

        /// <summary>Плитка героя в столбике слева.</summary>
        private void StageRosterTile(Button b, Label name, bool active)
        {
            if (!StageDressed) return;
            StagePlaque(b, dense: active);
            StageWord(name, active, LvnTokens.TextSm);
            LvnStyler.Chosen(b, active, LvnTokens.Gold);
        }

        /// <summary>Слово эмоции в столбике справа.</summary>
        private void StageEmotionChip(Button b, bool on)
        {
            if (!StageDressed) return;
            StagePlaque(b, dense: on);
            StageWord(b, on, LvnTokens.TextSm);
            LvnStyler.Chosen(b, on, LvnTokens.Gold);
        }

        /// <summary>Раздел (вкладка оси) в ряду: сама кнопка прозрачна — плашка
        /// у всего ряда, слово золотом у выбранного. Шрифт и кегль — ПРЕЖНИЕ:
        /// шрифт витрины шире, ряд переставал влезать, и подгонка (FitTabs)
        /// ужимала разделы («категории крупнее сделать, как были» — Илья 08.09).</summary>
        private void StageSectionTab(Button b, Label label, bool active)
        {
            if (!StageDressed) return;
            b.style.backgroundColor = Color.clear;
            b.style.backgroundImage = new StyleBackground(StyleKeyword.None);
            LvnChrome.ClearBorder(b);
            if (label == null) return;
            label.style.color = active ? LvnTokens.Gold : LvnTokens.TextDim;
            if (_tabFit == 0) label.style.fontSize = LvnTokens.TextLg;   // ужатый ряд держит свой кегль (ApplyTabFit)
        }

        /// <summary>Общее переодевание листа после сборки под героя: ряд
        /// разделов, подпись предмета, кнопки «отмена» и «надеть/купить».</summary>
        private void StageDress()
        {
            if (!StageDressed) return;
            // подложка разделов ПРОЗРАЧНАЯ, чуть темнее стекла листа (Илья 08.09)
            StagePlaque(_tabs, alpha: 0.35f);
            StagePlaque(_itemName);
            _itemName.style.color = LvnTokens.Text;
            LvnFonts.Apply(_itemName, LvnFonts.Display);
            StagePlaque(_cancel);
            StageWord(_cancel, false, LvnTokens.TextBase);
            StagePlaque(_confirm, dense: true, radiusDp: 6f);
            LvnStyler.Chosen(_confirm, true, LvnTokens.Gold);
            if (_confirmLabel != null) StageWord(_confirmLabel, true, LvnTokens.TextBase);
        }
    }
}
