using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// A full-screen container inset to <see cref="Screen.safeArea"/> — chrome
    /// placed inside never hides under a notch / punch-hole / home indicator,
    /// while full-bleed layers (backgrounds, veils, the stage) stay OUTSIDE it
    /// and keep covering the whole screen.
    ///
    /// The insets are applied as the element's own absolute left/top/right/bottom
    /// (NOT padding): most chrome children are absolutely positioned, and
    /// absolute children ignore a parent's padding but respect its box.
    ///
    /// Screen.safeArea is bottom-left-origin screen pixels; the panel is
    /// top-left-origin panel points — so Y inverts, and every inset goes through
    /// <see cref="RuntimePanelUtils.ScreenToPanel"/> to survive panel scaling.
    /// Recomputed on attach, on geometry changes, and on a slow tick (rotation
    /// and fold changes don't raise a UITK event by themselves).
    /// </summary>
    public sealed class SafeAreaElement : VisualElement
    {
        private Rect _applied = Rect.zero;
        private float _appliedWidth = -1f;

        /// <summary>ПОЛОСА НЕ ШИРЕ ЭТОГО (единицы панели; 0 — во всю ширину).
        /// На планшете на боку холст матчится по высоте и тянется вширь:
        /// реплика растягивалась через весь экран, кнопки выбора расходились на
        /// полметра. Ограничение живёт ЗДЕСЬ, а не сдвигом снаружи: элемент
        /// сам пишет свои left/right при каждом пересчёте выреза, и внешний
        /// «left 50 % + translate −50 %» после этого оставался половинным
        /// сдвигом без компенсации — реплика уезжала за левый край (скрин Ильи
        /// 09.09). Полоса считается полями от краёв — см. <see cref="BandInset"/>.</summary>
        public float MaxWidth;

        public SafeAreaElement()
        {
            name = "safe-area";
            pickingMode = PickingMode.Ignore; // container itself never eats taps
            LvnChrome.Stretch(this);

            RegisterCallback<AttachToPanelEvent>(_ => Refresh());
            RegisterCallback<GeometryChangedEvent>(_ => Refresh());
            // Rotation/fold watchdog — cheap compare, style writes only on change.
            schedule.Execute(Refresh).Every(500);
        }

        private void Refresh()
        {
            if (panel == null) return;
            // Через Кромочника: у выреза один источник, и подставленный для
            // снимков вырез обязан доехать и до сцены.
            var safe = LvnEdges.SafeArea;
            float pw = panel.visualTree?.resolvedStyle.width ?? 0f;
            if (float.IsNaN(pw)) pw = 0f;
            if (safe == _applied && Mathf.Approximately(pw, _appliedWidth)) return;
            _applied = safe; _appliedWidth = pw;

            float sw = Screen.width, sh = Screen.height;
            // Insets as screen-pixel distances from each edge, converted to panel
            // points. ScreenToPanel maps positions, which for a scale-only runtime
            // panel is exactly the scale transform we need for distances too.
            var leftTop = RuntimePanelUtils.ScreenToPanel(
                panel, new Vector2(safe.xMin, sh - safe.yMax));
            var rightBottom = RuntimePanelUtils.ScreenToPanel(
                panel, new Vector2(sw - safe.xMax, safe.yMin));

            float band = BandInset(pw, MaxWidth);
            style.left = Mathf.Max(band, leftTop.x);
            style.top = Mathf.Max(0f, leftTop.y);
            style.right = Mathf.Max(band, rightBottom.x);
            style.bottom = Mathf.Max(0f, rightBottom.y);
        }

        /// <summary>Поле с каждого края, чтобы полоса была не шире
        /// <paramref name="maxWidth"/>: на телефоне ноль, на широком экране —
        /// половина остатка. Ширина ещё не посчитана (0) — тоже ноль.</summary>
        public static float BandInset(float panelWidth, float maxWidth)
            => maxWidth > 0f && panelWidth > maxWidth ? (panelWidth - maxWidth) * 0.5f : 0f;
    }
}
