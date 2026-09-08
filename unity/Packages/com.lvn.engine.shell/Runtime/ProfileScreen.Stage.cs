using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ОБЛИК «СЦЕНА» ДЛЯ ПРОФИЛЯ («профиль тоже замени» — Илья 08.09). Те же
    /// приёмы, что у гардероба (WardrobeSheet.Stage): лист — стекло сцены,
    /// карточки и ряды — тёмные плашки тона панели без кромок, заголовки —
    /// золотом шрифтом витрины. Признак облика приходит с манифестом
    /// (<c>ui.browse.skin</c>): экран отмечен <see cref="ILvnContentAware"/>,
    /// как магазин. Без облика всё как было.
    /// </summary>
    public sealed partial class ProfileScreen : ILvnContentAware
    {
        private string _skin;
        private VisualElement _sheet;
        private Label _title;
        private bool _stageGlass;
        private bool StageDressed => !string.IsNullOrEmpty(_skin);

        public void SetContent(LvnManifest manifest)
        {
            var skin = manifest?.ui?.browse?.skin;
            if (skin == _skin) return;
            _skin = skin;
            StageSheet();
            Rebuild();
        }

        /// <summary>Лист профиля — стекло сцены вместо глухой заливки.</summary>
        private void StageSheet()
        {
            if (!StageDressed || _sheet == null || _stageGlass) return;
            _stageGlass = true;
            LvnStageKit.GlassSheet(_sheet, _skin, _assets, LvnTokens.Radius);
            // лист выше на 50 px: лента облика выше дырки, заложенной в HubTabSheet,
            // и наезжала на низ листа («профиль подними на 50 пикселей, как в
            // гардеробе, а то меню наезжает» — Илья 08.09)
            _sheet.style.bottom = _sheet.style.bottom.value.value + 50f;
            if (_title != null) StageHeader(_title);
        }

        /// <summary>Карточка или ряд — плашка облика.</summary>
        private T StageCard<T>(T el) where T : VisualElement
        {
            if (!StageDressed || el == null) return el;
            el.style.backgroundColor = UiColor.WithAlpha(LvnTokens.PanelBg, 0.82f);
            el.style.backgroundImage = new StyleBackground(StyleKeyword.None);
            LvnChrome.ClearBorder(el);
            LvnChrome.Round(el, LvnStageKit.D(8f));
            return el;
        }

        /// <summary>Заголовок раздела — золотом, шрифтом витрины.</summary>
        private Label StageHeader(Label l)
        {
            if (!StageDressed || l == null) return l;
            l.style.color = LvnTokens.Gold;
            LvnFonts.Apply(l, LvnFonts.Display);
            return l;
        }
    }
}
