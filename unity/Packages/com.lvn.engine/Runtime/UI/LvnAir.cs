using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ВОЗДУХ — отступы внутри элемента и снаружи него.
    ///
    /// <para>Самая крупная копипаста вёрстки в движке: «поставить внутренние
    /// отступы» записано четырьмя строками подряд в ПЯТИДЕСЯТИ ЧЕТЫРЁХ местах,
    /// и почти всегда одинаково — левый равен правому, верхний равен нижнему.
    /// Различает места только пара чисел, и она тонет среди восьми повторов
    /// слова <c>padding</c>.</para>
    ///
    /// <para>Цена не в длине. Четыре строки правятся по одной, и «поправил
    /// три, забыл четвёртую» даёт перекос, который видно только глазами и
    /// только на устройстве: элемент сдвигается на пару пикселей и перестаёт
    /// стоять в одну линию с соседом.</para>
    ///
    /// <para><b>Почему нет формы с двумя числами.</b> <c>Pad(el, 10, 20)</c>
    /// читается двояко: в CSS первым идёт ВЕРТИКАЛЬНЫЙ отступ, а в словаре
    /// нашей темы (<c>PanelPaddingX</c>, <c>PanelPaddingY</c>) первым стоит
    /// горизонтальный. Перепутанный порядок не падает и не краснеет — он даёт
    /// чуть другую вёрстку. Поэтому осей две и обе названы:
    /// <see cref="PadX"/> и <see cref="PadY"/>.</para>
    /// </summary>
    public static class LvnAir
    {
        /// <summary>Внутренний отступ со всех четырёх сторон.</summary>
        public static void Pad(VisualElement el, float all)
        {
            if (el == null) return;
            el.style.paddingLeft = all;
            el.style.paddingRight = all;
            el.style.paddingTop = all;
            el.style.paddingBottom = all;
        }

        /// <summary>Внутренний отступ ДВУМЯ ЧИСЛАМИ: по бокам и по высоте.
        ///
        /// <para>Заведено 01.09 по замеру: пара <c>PadX</c> + <c>PadY</c> на
        /// одном и том же элементе стояла 39 раз в 29 файлах — самая частая
        /// последовательность во всей оболочке. Отступ прямоугольника — ОДНО
        /// решение, и записывать его двумя строками значило оставлять
        /// вызывающему шанс написать вторую с другим элементом или забыть
        /// вовсе.</para></summary>
        public static void Pad(VisualElement el, float x, float y)
        {
            if (el == null) return;
            el.style.paddingLeft = x;
            el.style.paddingRight = x;
            el.style.paddingTop = y;
            el.style.paddingBottom = y;
        }

        /// <summary>Внутренний отступ по бокам: слева и справа поровну.</summary>
        public static void PadX(VisualElement el, float x)
        {
            if (el == null) return;
            el.style.paddingLeft = x;
            el.style.paddingRight = x;
        }

        /// <summary>Внутренний отступ сверху и снизу поровну.</summary>
        public static void PadY(VisualElement el, float y)
        {
            if (el == null) return;
            el.style.paddingTop = y;
            el.style.paddingBottom = y;
        }

        /// <summary>Внешний отступ со всех четырёх сторон.</summary>
        public static void Margin(VisualElement el, float all)
        {
            if (el == null) return;
            el.style.marginLeft = all;
            el.style.marginRight = all;
            el.style.marginTop = all;
            el.style.marginBottom = all;
        }

        /// <summary>Внешний отступ по бокам.</summary>
        public static void MarginX(VisualElement el, float x)
        {
            if (el == null) return;
            el.style.marginLeft = x;
            el.style.marginRight = x;
        }

        /// <summary>Внешний отступ сверху и снизу — одинаковый.</summary>
        public static void MarginY(VisualElement el, float y) => MarginY(el, y, y);

        /// <summary>Внешний отступ сверху и снизу — РАЗНЫЙ.
        ///
        /// <para>Дом умел только одинаковый, и потому его обходили: семь мест
        /// ставили `marginTop` и `marginBottom` сырыми стилями, потому что
        /// подпись жмётся к заголовку сверху и отпускает содержимое снизу — у
        /// вертикального ритма стороны неравны почти всегда.</para>
        ///
        /// <para>Это не лень вызывающих, а НЕПОЛНОТА дома: пока он не умеет
        /// нужного, каждый решает сам, и правило «отступы из токенов» держится
        /// на внимательности. Порядок доводов такой же, как в CSS, — сверху
        /// вниз; одно из семи мест писало снизу вверх, и читалось это как
        /// ошибка.</para>
        /// </summary>
        public static void MarginY(VisualElement el, float top, float bottom)
        {
            if (el == null) return;
            el.style.marginTop = top;
            el.style.marginBottom = bottom;
        }
    }
}
