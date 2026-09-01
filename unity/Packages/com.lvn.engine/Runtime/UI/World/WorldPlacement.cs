using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Maps a screen-fraction <see cref="Placement"/> onto a uGUI
    /// <see cref="RectTransform"/> — the Canvas mirror of the UITK math in
    /// <see cref="Placement"/>. The slot is anchored to the top-left of a content
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

        /// <summary>Растянуть узел на весь родительский слот, пяткой вниз.
        /// Так строится КАЖДЫЙ внутренний узел актёра — переход, rig, композит:
        /// они не имеют своего места на сцене, место принадлежит слоту. Пивот
        /// внизу по центру, чтобы поворот и масштаб шли от ног, а не от пояса.</summary>
        public static void Stretch(RectTransform rt) => Fill(rt, new Vector2(0.5f, 0f));

        /// <summary>РАСТЯНУТЬ НА ВЕСЬ РОДИТЕЛЬСКИЙ ПРЯМОУГОЛЬНИК — якоря по
        /// углам, нулевые отступы.
        ///
        /// <para>Растягивали дважды: узлы актёра и полотно фона. Отличие было
        /// РОВНО одно — пивот, и оно осмысленно: у фигуры он внизу по центру
        /// (поворот и масштаб идут от ног, а не от пояса), у полотна в центре.
        /// Поэтому пивот приходит доводом, а растяжка живёт здесь: забудь одну
        /// из четырёх строк, и узел схлопнется в точку — на глаз это выглядит
        /// как «актёр не появился», и искать будут в загрузке арта.</para>
        /// </summary>
        public static void Fill(RectTransform rt, Vector2 pivot)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.pivot = pivot;
        }

        public static void Apply(RectTransform slot, Placement p, Vector2 size)
        {
            // Top-left anchor so Y grows downward in canvas units, matching the
            // top-down coordinate the language uses (y=0 — верх экрана).
            slot.anchorMin = slot.anchorMax = new Vector2(0f, 1f);
            float w = (p.Width ?? DefaultWidth) * size.x;
            float h = (p.Height ?? DefaultHeight) * size.y;
            // Aspect-locked box (layered/boned art): fit within the placed bounds.
            //
            // ШИРИНА МЕРИТ ФИГУРУ, ВЫСОТА — КАДР. Это не прихоть, а два разных
            // смысла у полей файла. Воздух ПО БОКАМ ничего не значит: художник
            // одному герою оставил 1%, другому — 23%, и пока ширину мерил холст,
            // второй выходил на четверть ниже первого при тех же w=/h= («героиня
            // маленькая»). Воздух СВЕРХУ, наоборот, и есть рост: персонажей рисуют
            // в общем кадре, ребёнок занимает половину холста — по холсту он и
            // должен быть вдвое ниже взрослого, а нормализация по фигуре сравняла
            // бы их. Поэтому ширина ограничивает ФИГУРУ, а высота остаётся долей
            // экрана, как её и писал автор.
            if (p.BoxAspect is float a && a > 0f)
            {
                float boxH = Mathf.Min(h, w / (p.FigureW * a));
                w = boxH * a;
                h = boxH;
            }

            // РОСТ В МЕТРАХ РЕШАЕТ ВСЁ САМ — и здесь, а не раньше, потому что
            // только здесь известен размер кадра. Доля кадра, которую занимает
            // ФИГУРА, — это метры ÷ высота сцены в метрах; холст под ней больше
            // ровно на воздух над головой (FigureH). Ширину при этом не даём
            // ограничивать рост: чужой w= из меню или гардероба поджимал бы
            // фигуру, и рост опять зависел бы от того, кто ставит.
            if (p.Meters > 0f && LvnScale.Sane)
            {
                float figure = LvnScale.Fraction(p.Meters) * size.y; // фигура в пикселях
                h = figure / Mathf.Max(0.01f, p.FigureH);            // холст под фигуру
                w = p.BoxAspect is float ar && ar > 0f
                    ? h * ar                                          // холст заперт своим аспектом
                    : (p.Width ?? DefaultWidth) * size.x;             // без аспекта — ширина как была
            }
            slot.sizeDelta = new Vector2(w, h);
            // uGUI pivot is measured from the bottom-left; the placement anchor is
            // from the top-left — flip Y. Якорь («ноги», «центр») ищется ВНУТРИ
            // ФИГУРЫ: у арта с воздухом под ногами низ холста — это не пол.
            slot.pivot = new Vector2(
                p.FigureX + p.AnchorX * p.FigureW,
                1f - (p.FigureY + p.AnchorY * p.FigureH));
            slot.anchoredPosition = new Vector2(p.X * size.x, -p.Y * size.y);
            // Flip mirrors on X; rotation negated so positive degrees read clockwise
            // (UITK's convention) on the Canvas.
            slot.localScale = new Vector3(p.Flip ? -1f : 1f, 1f, 1f);
            slot.localEulerAngles = new Vector3(0f, 0f, -p.Rotation);
        }
    }
}
