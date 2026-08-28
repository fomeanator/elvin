using Lvn.UI.World;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// МАТОВОЕ СТЕКЛО ПОД ЭЛЕМЕНТОМ ИНТЕРФЕЙСА.
    ///
    /// <para>У <see cref="VisualElement"/> нет материала, поэтому «шейдер на
    /// диалоговом окне» звучит как повод переносить окно на канвас. Повода нет:
    /// фоном элемента может быть <see cref="RenderTexture"/>, а в текстуру умеет
    /// рисовать что угодно. <see cref="LvnGlass"/> кладёт туда размытый кадр —
    /// здесь этот кадр надевается на элемент так, чтобы кусок подложки под ним
    /// совпал с тем, что за ним на самом деле.</para>
    ///
    /// <para>Совмещение и есть вся хитрость. Подложка — это ВЕСЬ экран, а окно
    /// занимает его часть, поэтому картинку надо растянуть на размер панели и
    /// сдвинуть на минус собственные координаты окна. Ошибись здесь — и стекло
    /// покажет правильное размытие не того места: эффект выглядит «почти
    /// правильным», и объяснить, что не так, сложнее, чем если бы он не работал
    /// вовсе.</para>
    ///
    /// <para>Раскладка не трогается: стекло — вложенный слой под содержимым,
    /// прозрачный для нажатий. Текст, ввод, типографика и биндинги
    /// <c>ui</c> остаются ровно теми же.</para>
    /// </summary>
    public static class UiGlass
    {
        private const string LayerName = "lvn-glass";
        private const string TintName = "lvn-glass-tint";

        /// <summary>Надеть стекло на <paramref name="host"/>.
        /// <paramref name="strength"/> 0 — снять (элемент возвращается к обычной
        /// заливке), 1 — стекло во всю силу. <paramref name="tint"/> — цвет,
        /// который ложится поверх размытия: без него стекло читается как дыра в
        /// экране, а не как поверхность.</summary>
        public static void Apply(VisualElement host, float strength, Color tint)
        {
            if (host == null) return;
            var layer = host.Q(LayerName);

            if (strength <= 0.004f)
            {
                Detach(layer);
                return;
            }

            if (layer == null)
            {
                layer = new VisualElement { name = LayerName, pickingMode = PickingMode.Ignore };
                LvnChrome.Stretch(layer);

                var tintLayer = new VisualElement { name = TintName, pickingMode = PickingMode.Ignore };
                LvnChrome.Stretch(tintLayer);
                layer.Add(tintLayer);

                host.Insert(0, layer);       // под содержимым, но внутри скругления
                // Скруглённое окно обрезает подложку только при overflow:hidden —
                // иначе размытый прямоугольник торчит из закруглённых углов.
                host.style.overflow = Overflow.Hidden;

                host.RegisterCallback<GeometryChangedEvent>(_ => Align(host));
                layer.RegisterCallback<DetachFromPanelEvent>(_ => LvnGlass.Current?.Forget());
                LvnGlass.Current?.Retain();

                // Подложка пересоздаётся при смене разрешения (поворот экрана,
                // окно на настольной машине), и старая ссылка после этого
                // показывает пустоту. Дешевле переспрашивать, чем изобретать
                // уведомление ради четырёх раз за сессию.
                layer.schedule.Execute(() => Bind(layer, host)).Every(250);
            }

            var t = tint;
            t.a *= Mathf.Clamp01(strength);
            var tl = layer.Q(TintName);
            if (tl != null) tl.style.backgroundColor = t;

            Bind(layer, host);
            Align(host);
        }

        /// <summary>Есть ли на элементе стекло (нужно тем, кто решает, красить ли
        /// его обычной заливкой).</summary>
        public static bool IsOn(VisualElement host) => host?.Q(LayerName) != null;

        private static void Detach(VisualElement layer)
        {
            if (layer == null) return;
            // Forget() позовёт DetachFromPanelEvent — считать пользователя здесь
            // ещё раз значит увести счётчик в минус и погасить стекло у соседа.
            layer.RemoveFromHierarchy();
        }

        private static void Bind(VisualElement layer, VisualElement host)
        {
            var rt = LvnGlass.Current?.Backdrop;
            if (rt == null)
            {
                // Нет камеры мира (тесты, экран без сцены) или первый кадр ещё не
                // отрисован: остаётся тонировка — окно выглядит обычным
                // полупрозрачным, а не пустой дырой.
                layer.style.backgroundImage = StyleKeyword.None;
                return;
            }
            var cur = layer.style.backgroundImage.value.renderTexture;
            if (!ReferenceEquals(cur, rt))
            {
                layer.style.backgroundImage = Background.FromRenderTexture(rt);
                Align(host);
            }
        }

        /// <summary>
        /// СОВМЕЩЕНИЕ — вся арифметика приёма, отдельно от того, кому её
        /// присвоить. Подложка это ВЕСЬ экран, окно занимает его часть: чтобы под
        /// окном оказался тот же кусок мира, что за ним, картинку растягивают на
        /// размер панели и сдвигают на МИНУС координаты окна.
        ///
        /// <para>Ошибка здесь не ломает ничего видимо — стекло просто показывает
        /// размытие не того места. Поэтому счёт живёт чистой функцией: его можно
        /// проверить без камеры, панели и единого кадра.</para>
        /// </summary>
        /// <param name="panel">Прямоугольник корня панели (это же весь экран).</param>
        /// <param name="box">Положение элемента в координатах панели.</param>
        public static (Vector2 size, Vector2 offset) Fit(Rect panel, Rect box) =>
            (new Vector2(panel.width, panel.height), new Vector2(-box.x, -box.y));

        private static void Align(VisualElement host)
        {
            var layer = host?.Q(LayerName);
            if (layer == null || host.panel == null) return;
            var panelRect = host.panel.visualTree.layout;
            if (panelRect.width <= 1f || panelRect.height <= 1f) return;

            var (size, offset) = Fit(panelRect, host.worldBound);
            layer.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(size.x, size.y));
            layer.style.backgroundPositionX = new StyleBackgroundPosition(
                new BackgroundPosition(BackgroundPositionKeyword.Left, offset.x));
            layer.style.backgroundPositionY = new StyleBackgroundPosition(
                new BackgroundPosition(BackgroundPositionKeyword.Top, offset.y));
        }
    }
}
