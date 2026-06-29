using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>Shared UI Toolkit styling helpers for the reference components, so
    /// the dialogue box and choice list skin their panels the same way.</summary>
    public static class UiStyle
    {
        /// <summary>Paint <paramref name="el"/> with a background sprite (optionally
        /// 9-sliced) instead of a flat colour. The sprite owns the element's corners
        /// and fill, so the solid colour and rounded radii are cleared. A null sprite
        /// leaves the element untouched, so the caller's colour fallback stands.</summary>
        public static void ApplyBackground(VisualElement el, Sprite sprite, int slice)
        {
            if (el == null || sprite == null) return;

            el.style.backgroundImage = new StyleBackground(sprite);
            el.style.backgroundColor = Color.clear; // let the art show, not a colour behind it
            // A framed sprite defines its own corners — drop the rounded-rect radii.
            el.style.borderTopLeftRadius = 0;
            el.style.borderTopRightRadius = 0;
            el.style.borderBottomLeftRadius = 0;
            el.style.borderBottomRightRadius = 0;

            if (slice > 0)
            {
                el.style.unitySliceLeft = slice;
                el.style.unitySliceRight = slice;
                el.style.unitySliceTop = slice;
                el.style.unitySliceBottom = slice;
            }
        }
    }
}
