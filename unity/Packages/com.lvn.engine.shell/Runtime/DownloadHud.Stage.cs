using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ОБЛИК «СЦЕНА» ДЛЯ ЛИСТА ЗАГРУЗОК («кнопки, тексты в нашем стиле надо»
    /// — Илья 10.09). Те же приёмы, что у профиля и детали новеллы: лист —
    /// рамка облика с полным фоном (card-back.png), заголовок и процент —
    /// золотом шрифтом витрины, ряды и график — тёмные плашки тона панели,
    /// кнопки — плашки с золотой гранью у главной.
    ///
    /// <para>Кружок — не экран набора, а деталь хрома, поэтому манифест ему
    /// вручает оболочка отдельной строкой (NovelShell). Без облика всё как
    /// было: каждое правило ниже включается только при <see cref="StageDressed"/>.</para>
    /// </summary>
    public sealed partial class DownloadHud : ILvnContentAware
    {
        private string _skin;
        private ILvnAssets _assets;
        private bool _stageSheet;
        private bool StageDressed => !string.IsNullOrEmpty(_skin);
        private static float D(float dp) => LvnStageKit.D(dp);

        /// <summary>Манифест и склад арта — рамке облика нужны оба.</summary>
        public void SetContent(LvnManifest manifest, ILvnAssets assets)
        {
            if (assets != null) _assets = assets;
            SetContent(manifest);
        }

        public void SetContent(LvnManifest manifest)
            => LvnStageKit.TakeSkin(manifest, ref _skin, () =>
            {
                StageSheet();
                if (_expanded) RebuildSections();
            });

        /// <summary>Лист одевается один раз: рамка на всю капсулу, поля по
        /// паспорту, слова и полоса — облика.</summary>
        private void StageSheet()
        {
            if (!StageDressed || _stageSheet) return;
            _stageSheet = true;

            LvnChrome.Stretch(_full);
            LvnStageKit.GlassSheet(_full, _skin, _assets, LvnTokens.Radius);

            StageHeading(_title, LvnTokens.TextLg);
            StageHeading(_percent, LvnTokens.TextDisplay);
            LvnFonts.Apply(_file, LvnFonts.Display);
            StageCaption(_speedTitle);
            StageCaption(_peak);
            StageCaption(_axisLeft);
            StageCaption(_axisRight);
            foreach (var c in _captions) StageCaption(c);
            StagePlaque(_chartBox);
            StagePlaque(_state, dense: true);
            LvnFonts.Apply(_state, LvnFonts.Display);
            _closeBtn.style.color = LvnTokens.Gold;

            // Полоса героя — полоса макета: чёрная дорожка, канавка, светлый ход.
            int at = _full.IndexOf(_bar);
            _bar.RemoveFromHierarchy();
            _bar = LvnStageKit.Progress(out _barFill);
            _bar.style.marginTop = LvnTokens.Space2;
            _full.Insert(at, _bar);
        }

        /// <summary>Плашка облика: тёмная заливка тона панели, скруглённая.
        /// Выбранному и чипу — плотнее.</summary>
        private static void StagePlaque(VisualElement el, bool dense = false)
            => LvnStyler.Plate(el, UiColor.WithAlpha(LvnTokens.PanelBg, dense ? 0.94f : 0.82f), LvnTokens.Text, D(8f));

        /// <summary>Заголовок — золотом, шрифтом витрины.</summary>
        private static Label StageHeading(Label l, float size)
        {
            if (l == null) return null;
            l.style.color = LvnTokens.Gold;
            l.style.fontSize = size;
            LvnFonts.Apply(l, LvnFonts.Display);
            return l;
        }

        /// <summary>Подпись — приглушённо, шрифтом витрины.</summary>
        private static Label StageCaption(Label l)
        {
            if (l == null) return null;
            l.style.color = LvnTokens.TextDim;
            LvnFonts.Apply(l, LvnFonts.Display);
            return l;
        }

        /// <summary>Чем подсвечен активный ряд и главная кнопка: золото облика,
        /// иначе акцент темы.</summary>
        private Color ChosenInk => StageDressed ? LvnTokens.Gold : LvnTokens.Accent;

        /// <summary>Слова кнопок облика идут прописными, как на рамках макета.</summary>
        private string Up(string s) => StageDressed ? (s ?? string.Empty).ToUpperInvariant() : s;

        /// <summary>Кнопка листа: без облика — плашка темы, с обликом — плашка
        /// витрины. Имя элемента остаётся: по нему ходят тесты и тур.</summary>
        private T DressButton<T>(T b, bool primary) where T : VisualElement
        {
            if (b == null) return null;
            if (StageDressed) return LvnStageKit.PlateButton(b, primary);
            if (primary) LvnStyler.Primary(b, LvnTokens.RadiusSm);
            else LvnStyler.Plate(b, LvnTokens.Faint, LvnTokens.TextDim, LvnTokens.RadiusSm);
            return b;
        }
    }
}
