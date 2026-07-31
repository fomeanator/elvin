using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Maps a screen-fraction <see cref="Placement"/> onto a uGUI
    /// <see cref="RectTransform"/> — the Canvas mirror of the UITK math in
    /// <see cref="ActorLayer"/>. The slot is anchored to the top-left of a content
    /// rect of <paramref name="size"/> canvas units; the object's
    /// <see cref="Placement.AnchorX"/>/<see cref="Placement.AnchorY"/> point lands
    /// on <see cref="Placement.X"/>/<see cref="Placement.Y"/> (both 0..1, Y from
    /// the top, just like UITK), sized by Width/Height, flipped and rotated.
    ///
    /// <para>Pure transform work and pure of any Canvas state, so it is unit-tested
    /// headlessly with a fixed content size.</para>
    /// </summary>
    public static class WorldPlacement
    {
        public const float DefaultWidth = Placement.DefaultWidth;  // one source of truth (standard VN framing)
        public const float DefaultHeight = Placement.DefaultHeight;

        public static void Apply(RectTransform slot, Placement p, Vector2 size)
        {
            // Top-left anchor so Y grows downward in canvas units, matching the
            // UITK top-down coordinate ActorLayer uses.
            slot.anchorMin = slot.anchorMax = new Vector2(0f, 1f);
            // uGUI pivot is measured from the bottom-left; the placement anchor is
            // from the top-left — flip Y.
            slot.pivot = new Vector2(p.AnchorX, 1f - p.AnchorY);
            // ГЛУБИНА: одно число вместо ручной пары «размер + порядок» на каждую
            // дистанцию. Спрайт на depth=0 стоит в плане камеры и рисуется в
            // натуральную величину; дальше — мельче по закону перспективы
            // (k = f / (f + depth), f — «фокус», условная дистанция до плана).
            // В зонной боёвке этим схлопываются девять почти одинаковых веток на
            // каждую позу врага.
            float depthK = DepthScale(p.Depth);
            float w = (p.Width ?? DefaultWidth) * size.x * depthK;
            float h = (p.Height ?? DefaultHeight) * size.y * depthK;
            // Aspect-locked box (layered/boned art): fit within the placed bounds.
            if (p.BoxAspect is float a && a > 0f)
            {
                if (w / h > a) w = h * a;
                else h = w / a;
            }
            slot.sizeDelta = new Vector2(w, h);
            slot.anchoredPosition = new Vector2(p.X * size.x, -p.Y * size.y);
            // Flip mirrors on X; rotation negated so positive degrees read clockwise
            // (UITK's convention) on the Canvas.
            slot.localScale = new Vector3(p.Flip ? -1f : 1f, 1f, 1f);
            slot.localEulerAngles = new Vector3(0f, 0f, -p.Rotation);
            // Дальний уходит ЗА ближнего сам: явный z по-прежнему главнее, но без
            // него порядок диктует глубина, и автору не нужно держать её в голове
            // дважды.
            if (p.Z == null && p.Depth is float d)
                slot.SetSiblingIndex(Mathf.Clamp(Mathf.RoundToInt(1000f - d * 10f), 0, slot.parent != null ? slot.parent.childCount - 1 : 0));
        }

        /// <summary>Перспективный коэффициент размера для глубины. «Фокус» 6 —
        /// подобран так, чтобы depth=6 давал половину роста: шаг глубины читается
        /// глазом, но фигура не схлопывается в точку на разумных дистанциях.</summary>
        public const float FocalDepth = 6f;

        public static float DepthScale(float? depth)
        {
            if (depth == null) return 1f;
            float d = Mathf.Max(depth.Value, -FocalDepth * 0.9f); // не даём вывернуть за камеру
            return FocalDepth / (FocalDepth + d);
        }
    }
}
