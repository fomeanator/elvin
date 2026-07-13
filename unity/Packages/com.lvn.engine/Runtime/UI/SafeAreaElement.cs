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

        public SafeAreaElement()
        {
            name = "safe-area";
            pickingMode = PickingMode.Ignore; // container itself never eats taps
            style.position = Position.Absolute;
            style.left = 0; style.top = 0; style.right = 0; style.bottom = 0;

            RegisterCallback<AttachToPanelEvent>(_ => Refresh());
            RegisterCallback<GeometryChangedEvent>(_ => Refresh());
            // Rotation/fold watchdog — cheap compare, style writes only on change.
            schedule.Execute(Refresh).Every(500);
        }

        private void Refresh()
        {
            if (panel == null) return;
            var safe = Screen.safeArea;
            if (safe == _applied) return;
            _applied = safe;

            float sw = Screen.width, sh = Screen.height;
            // Insets as screen-pixel distances from each edge, converted to panel
            // points. ScreenToPanel maps positions, which for a scale-only runtime
            // panel is exactly the scale transform we need for distances too.
            var leftTop = RuntimePanelUtils.ScreenToPanel(
                panel, new Vector2(safe.xMin, sh - safe.yMax));
            var rightBottom = RuntimePanelUtils.ScreenToPanel(
                panel, new Vector2(sw - safe.xMax, safe.yMin));

            style.left = Mathf.Max(0f, leftTop.x);
            style.top = Mathf.Max(0f, leftTop.y);
            style.right = Mathf.Max(0f, rightBottom.x);
            style.bottom = Mathf.Max(0f, rightBottom.y);
        }
    }
}
