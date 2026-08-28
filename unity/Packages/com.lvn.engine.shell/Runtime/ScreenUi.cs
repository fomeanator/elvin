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

        /// <summary>
        /// The substantial lower sheet used by menu sections that live over the
        /// heroine and the scene canvas. This is deliberately not a glowing card:
        /// the sheet is the one object allowed to have a strong edge, while its
        /// contents stay quiet and let the scene remain the hero.
        /// </summary>
        public static void SceneSheet(VisualElement el, float opacity = 0.94f)
        {
            if (el == null) return;
            var bg = LvnTokens.PanelBg;
            el.style.backgroundColor = new Color(bg.r, bg.g, bg.b, opacity);
            LvnChrome.Round(el, LvnTokens.Radius + 4f);
            var edge = LvnTokens.Accent;
            LvnChrome.Border(el, new Color(edge.r, edge.g, edge.b, 0.30f), 1f);
            el.style.borderTopWidth = 2f;
            el.style.borderTopColor = new Color(edge.r, edge.g, edge.b, 0.72f);
        }

        /// <summary>Load a sprite by url and set it as the element's background
        /// image. Missing art is non-fatal — the element keeps whatever it had.</summary>
        /// <summary>
        /// ПОКАЗАТЬ КАРТИНКУ — одно действие, а не запуск задачи с меткой.
        ///
        /// <para>Загрузка асинхронна, но для экрана это не событие: он говорит
        /// «здесь эта обложка» и идёт дальше. Тридцать четыре места писали это
        /// одинаково — <c>LvnAsync.Fire(ScreenUi.AssignBgAsync(el, url, assets),
        /// "AssignBg")</c>, — и тридцать три из них с одной и той же меткой.
        /// Метка в диагностике при этом бесполезна ровно потому, что общая: по
        /// строке «AssignBg» в логе не понять, чья картинка не доехала.</para>
        ///
        /// <para>Асинхронность — свойство загрузки, а не решение вызывающего.
        /// Пока «запусти и забудь» пишется на месте, каждый обязан помнить и про
        /// <c>Fire</c> (иначе задача уплывёт без обработки отказа), и про метку.
        /// Забыть можно только вторую — и её забывали в пользу общей.</para>
        ///
        /// <para>Метку принимаем: у меню полотна она своя («MenuCanvas»), и
        /// разница осмысленная — это единственный фон, чья пропажа видна на
        /// весь экран.</para>
        /// </summary>
        public static void SetBg(VisualElement el, string url, ILvnAssets assets, string what = "AssignBg")
            => LvnAsync.Fire(AssignBgAsync(el, url, assets), what);

        public static async Task AssignBgAsync(VisualElement el, string url, ILvnAssets assets)
        {
            if (el == null || string.IsNullOrEmpty(url) || assets == null) return;
            try
            {
                var sprite = await assets.LoadSpriteAsync(url, CancellationToken.None);
                if (sprite != null)
                {
                    el.style.backgroundImage = new StyleBackground(sprite);
                    PinBg(el, sprite, assets);
                }
            }
            catch { /* missing art is non-fatal */ }
        }

        // ── ЖИВЫЕ ФОНЫ UI ЗАКРЕПЛЕНЫ (27.08): LRU спрайт-кэша уничтожал
        // текстуры, которые элементы ещё показывают, — обложки новелл в хабе
        // белели после прогулки по гардеробу, арт героини после главы (живые
        // репорты). Правило то же, что у сцены: что на экране — не трогать.
        // Пин живёт, пока элемент в панели; замена арта и уход из иерархии
        // снимают его.
        private static readonly System.Collections.Generic.Dictionary<
            VisualElement, (Lvn.Content.ContentLoader loader, Sprite sprite)> _bgPins
            = new System.Collections.Generic.Dictionary<
                VisualElement, (Lvn.Content.ContentLoader, Sprite)>();

        internal static void PinBg(VisualElement el, Sprite sprite, ILvnAssets assets)
        {
            var loader = (assets as Lvn.UI.CachingAssets)?.Loader;
            if (loader == null || sprite == null) return;
            if (_bgPins.TryGetValue(el, out var old))
            {
                if (ReferenceEquals(old.sprite, sprite)) return;
                old.loader?.PinSprite(old.sprite, false);
                loader.PinSprite(sprite, true);
                _bgPins[el] = (loader, sprite);
                return;
            }
            loader.PinSprite(sprite, true);
            _bgPins[el] = (loader, sprite);
            el.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                // Guard по словарю: дубль-колбэк после повторного Attach
                // становится no-op — пин снимается ровно один раз.
                if (_bgPins.TryGetValue(el, out var cur))
                {
                    cur.loader?.PinSprite(cur.sprite, false);
                    _bgPins.Remove(el);
                }
            });
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
                PinBg(el, sprite, assets);
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
            => Lvn.UI.LvnEdges.Insets(el);   // окно в Кромочника

        /// <summary>ОТСТУП СВЕРХУ — сколько единиц панели занимает вырез камеры
        /// (чёлка, «остров», статус-бар).
        ///
        /// <para>Считался в четырёх местах и ДВУМЯ разными формулами: одни
        /// переводили пиксели через RuntimePanelUtils, другие — множителем
        /// «высота панели / высота экрана». На обычном телефоне это одно и то
        /// же число, но стоит панели получить нестандартный масштаб, и элементы,
        /// которые обязаны стоять на одной линии (бар, колонка эмоций, шапка
        /// хаба), разъезжаются.</para></summary>
        public static float SafeTop(VisualElement el) => Lvn.UI.LvnEdges.Insets(el).x;

        // Экранные пиксели по вертикали → единицы панели. ScreenToPanel
        // отображает позиции, но для scale-only рантайм-панели это ровно тот
        // масштаб, который нужен и для расстояний.
        private static float ToPanel(IPanel panel, float pixels)
            => Mathf.Max(0f, RuntimePanelUtils.ScreenToPanel(panel, new Vector2(0f, pixels)).y);

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
