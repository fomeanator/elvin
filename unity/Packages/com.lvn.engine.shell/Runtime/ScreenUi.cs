using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// Shared UI Toolkit building blocks for the shell screens. Every screen used
    /// to re-declare these privately — the stretch-to-fill layout, the async
    /// background loader, and a couple of label helpers — so they lived in five or
    /// six copies. Centralising them keeps the screens to their actual layout and
    /// removes the drift that copy-paste invites.
    /// </summary>
    internal static class ScreenUi
    {
        /// <summary>Pin an element to all four edges of its parent (absolute,
        /// full-bleed). Returns the element so it can be used inline.</summary>
        public static T Stretch<T>(T el) where T : VisualElement
        {
            el.style.position = Position.Absolute;
            el.style.left = 0;
            el.style.right = 0;
            el.style.top = 0;
            el.style.bottom = 0;
            return el;
        }

        /// <summary>Load a sprite by url and set it as the element's background
        /// image. Missing art is non-fatal — the element keeps whatever it had.</summary>
        public static async Task AssignBgAsync(VisualElement el, string url, ILvnAssets assets)
        {
            if (el == null || string.IsNullOrEmpty(url) || assets == null) return;
            try
            {
                var sprite = await assets.LoadSpriteAsync(url, CancellationToken.None);
                if (sprite != null) el.style.backgroundImage = new StyleBackground(sprite);
            }
            catch { /* missing art is non-fatal */ }
        }

        /// <summary>Load a sprite and apply it as the element's 9-sliced frame —
        /// the art replaces the flat fill. Missing art keeps the flat look.</summary>
        public static async Task AssignNineSliceAsync(VisualElement el, string url, int slice, ILvnAssets assets)
        {
            if (el == null || string.IsNullOrEmpty(url) || assets == null) return;
            try
            {
                var sprite = await assets.LoadSpriteAsync(url, CancellationToken.None);
                if (sprite == null) return;
                el.style.backgroundImage = new StyleBackground(sprite);
                el.style.backgroundColor = Color.clear;
                if (slice > 0)
                {
                    el.style.unitySliceLeft = slice;
                    el.style.unitySliceRight = slice;
                    el.style.unitySliceTop = slice;
                    el.style.unitySliceBottom = slice;
                }
            }
            catch { /* missing art is non-fatal */ }
        }

        /// <summary>A full-width, centre-aligned absolute label placed at a vertical
        /// fraction of its parent. Ignores pointer input (overlay text).</summary>
        public static Label CenterLabel(float topFraction, Color color, float size)
        {
            var l = new Label();
            l.style.position = Position.Absolute;
            l.style.left = 0;
            l.style.right = 0;
            l.style.top = Length.Percent(topFraction * 100f);
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            l.style.color = color;
            l.style.fontSize = size;
            l.pickingMode = PickingMode.Ignore;
            return l;
        }

        /// <summary>Null-safe label text setter.</summary>
        public static void SetText(Label l, string t) { if (l != null) l.text = t; }

        /// <summary>The device safe-area insets (notch / home indicator) converted
        /// to panel units for <paramref name="el"/>'s panel: x = top, y = bottom.
        /// Zero before the element is attached (or on notchless screens) — call it
        /// from a <see cref="GeometryChangedEvent"/> so it re-resolves once real.</summary>
        public static Vector2 SafeVerticalInsets(VisualElement el)
        {
            var panel = el?.panel;
            if (panel == null || Screen.height <= 0) return Vector2.zero;
            float panelH = panel.visualTree.layout.height;
            if (float.IsNaN(panelH) || panelH <= 0) return Vector2.zero;
            float scale = panelH / Screen.height;
            var safe = Screen.safeArea;
            float topPx = Screen.height - safe.yMax;
            float bottomPx = safe.yMin;
            return new Vector2(Mathf.Max(0f, topPx * scale), Mathf.Max(0f, bottomPx * scale));
        }

        /// <summary>Build a horizontal progress bar centred on (<paramref name="xFrac"/>,
        /// <paramref name="yFrac"/>) of its parent, sized <paramref name="wFrac"/>×
        /// <paramref name="hFrac"/>: a coloured <paramref name="track"/> under a
        /// left-anchored <paramref name="fill"/> the caller animates by setting its
        /// width. Both the boot splash and the chapter loader built this identically;
        /// callers add their own extras (a frame overlay, art) on top.</summary>
        public static VisualElement ProgressBar(
            float xFrac, float yFrac, float wFrac, float hFrac,
            Color trackColor, Color fillColor,
            out VisualElement track, out VisualElement fill)
        {
            var bar = new VisualElement();
            bar.style.position = Position.Absolute;
            bar.style.left = Length.Percent(xFrac * 100f);
            bar.style.top = Length.Percent(yFrac * 100f);
            bar.style.width = Length.Percent(wFrac * 100f);
            bar.style.height = Length.Percent(hFrac * 100f);
            bar.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f), 0f);
            bar.pickingMode = PickingMode.Ignore;

            track = Stretch(new VisualElement());
            track.style.backgroundColor = trackColor;
            bar.Add(track);

            fill = new VisualElement();
            fill.style.position = Position.Absolute;
            fill.style.left = 0;
            fill.style.top = 0;
            fill.style.bottom = 0;
            fill.style.width = Length.Percent(0f);
            fill.style.backgroundColor = fillColor;
            bar.Add(fill);

            return bar;
        }
    }
}
