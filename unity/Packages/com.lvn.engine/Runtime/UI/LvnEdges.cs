using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// КРОМОЧНИК — где у экрана края и сколько от них отступать.
    ///
    /// <para>Вырез камеры считать умели двое (<c>ScreenUi.SafeTop</c> и
    /// <see cref="SafeAreaElement"/>), а вот РЕШЕНИЕ «сколько воздуха оставить»
    /// принимали пятеро, и каждый по-своему: шапка хаба брала
    /// <c>Max(52, вырез + 12)</c>, страницы разделов — <c>Max(28, вырез + 12)</c>,
    /// нижняя навигация — <c>вырез + 6</c>, кружок загрузок — <c>вырез + 5</c>, а
    /// игровой HUD вёл собственное поле и свой пересчёт. Числа разные не потому,
    /// что так задумано: их подбирали по одному, каждое на своём скриншоте.</para>
    ///
    /// <para>Хуже разнобоя в числах — разнобой в ПОВОДЕ пересчитать. Одни ловили
    /// <c>GeometryChangedEvent</c>, другой тикал раз в полсекунды, третьего
    /// кормил хост вызовом <c>SetSafeTop</c>. Поворот экрана и складной телефон
    /// не поднимают события сами, поэтому часть поверхностей узнавала о новой
    /// кромке, а часть — нет, и они разъезжались по одной линии.</para>
    ///
    /// <para>Здесь и то, и другое: КРОМКИ (в единицах панели, с учётом её
    /// масштаба) и ПОВОД — подписка, которая будит поверхность на всех трёх
    /// случаях сразу и молчит, пока ничего не изменилось.</para>
    /// </summary>
    public static class LvnEdges
    {
        /// <summary>Воздух под верхним вырезом для ПОЛНОЙ страницы (шапка с
        /// названием). Историческое число хаба.</summary>
        public const float PageTopAir = 12f;
        /// <summary>Минимум сверху у главной — её шапка живёт крупно.</summary>
        public const float HomeTopMin = 52f;
        /// <summary>Минимум сверху у внутренних страниц (список, деталь).</summary>
        public const float PageTopMin = 28f;
        /// <summary>Воздух над домашней полосой снизу (нижняя навигация).</summary>
        public const float NavBottomAir = 6f;
        /// <summary>Поле страницы от боковых краёв экрана: витрина, сборник,
        /// деталь. Шире нижней ступени шкалы намеренно — это поле СТРАНИЦЫ, а
        /// не воздух между элементами, и мерить его шкалой отступов
        /// неправильно. Стояло пятью числами по месту.</summary>
        public const float PageSide = 30f;

        /// <summary>Вырезы устройства в единицах панели: x = сверху, y = снизу.
        /// Ноль, пока элемент не в панели (и на экранах без выреза).</summary>
        public static Vector2 Insets(VisualElement el)
        {
            var panel = el?.panel;
            if (panel == null || Screen.height <= 0) return Vector2.zero;
            var safe = Screen.safeArea;
            return new Vector2(ToPanel(panel, Screen.height - safe.yMax),
                               ToPanel(panel, safe.yMin));
        }

        /// <summary>Отступ сверху: вырез плюс воздух, но не меньше минимума.</summary>
        public static float Top(VisualElement el, float minimum = 0f, float air = 0f)
            => Mathf.Max(minimum, Insets(el).x + air);

        /// <summary>Отступ снизу: вырез (домашняя полоса) плюс воздух.</summary>
        public static float Bottom(VisualElement el, float air = 0f)
            => Insets(el).y + air;

        /// <summary>
        /// НЕ НЫРЯТЬ ПОД НАКЛАДКУ — освободить снизу ровно столько, сколько
        /// занимает элемент, лежащий поверх.
        ///
        /// <para>Высота накладки была ЧИСЛОМ у того, кто под ней: лента витрины
        /// оставляла себе 124 пикселя, «чтобы не нырять под меню». Число
        /// измерили рукой один раз, и оно описывает не решение, а ЧУЖУЮ высоту
        /// — то есть тот же факт, что и вёрстка самого меню, только записанный
        /// второй раз и в другом файле.</para>
        ///
        /// <para>Расходятся такие пары молча. Нижнее меню растёт от размера
        /// шрифта интерфейса (у игрока эта ручка есть в настройках), и на
        /// крупном размере лента начинает нырять — ровно то, что число должно
        /// было предотвратить. Спрашивать надо у накладки, а не помнить за
        /// неё.</para>
        /// </summary>
        public static void Under(VisualElement surface, VisualElement blocker, float air = 0f)
        {
            if (surface == null || blocker == null) return;
            void Apply()
            {
                float h = blocker.resolvedStyle.height;
                // До первой раскладки высоты нет — оставляем прежнее значение:
                // ноль здесь означал бы «накладки нет», а она есть.
                if (float.IsNaN(h) || h <= 0f) return;
                surface.style.paddingBottom = h + air;
            }
            blocker.RegisterCallback<GeometryChangedEvent>(_ => Apply());
            surface.RegisterCallback<AttachToPanelEvent>(_ => Apply());
            Apply();
        }

        /// <summary>
        /// СЛЕДИТЬ ЗА КРОМКОЙ: вызвать <paramref name="apply"/> сейчас и потом
        /// при каждом её изменении.
        ///
        /// <para>Поводов три, и поодиночке ни один не полон: привязка к панели
        /// (до неё вырезы неизвестны), смена геометрии и медленный тик —
        /// поворот экрана и раскладывание складного телефона UITK-событием не
        /// сопровождаются. Стили пишутся только на изменение, поэтому тик
        /// ничего не стоит.</para>
        /// </summary>
        public static void Follow(VisualElement el, Action<Vector2> apply,
                                  long tickMs = 500)
        {
            if (el == null || apply == null) return;
            var applied = new Vector2(float.NaN, float.NaN);
            void Refresh()
            {
                // ПОКА ЭЛЕМЕНТА НЕТ В ПАНЕЛИ, СЛЕДИТЬ НЕ ЗА ЧЕМ. Первый вызов
                // раньше шёл СРАЗУ, из конструктора подписчика: вырезов там ещё
                // не существует (Insets вернёт ноль), зато сам подписчик ещё не
                // достроен — и «примени отступ» приходило к полю, которого нет.
                // Живой бут падал NullReferenceException в кружке загрузок
                // (28.08), причём падал ВЕСЬ бут: исключение из конструктора
                // некому поймать.
                if (el.panel == null) return;
                var now = Insets(el);
                if (Mathf.Approximately(now.x, applied.x) && Mathf.Approximately(now.y, applied.y))
                    return;
                applied = now;
                apply(now);
            }
            el.RegisterCallback<AttachToPanelEvent>(_ => Refresh());
            el.RegisterCallback<GeometryChangedEvent>(_ => Refresh());
            el.schedule.Execute(Refresh).Every(tickMs);
            Refresh();
        }

        // Экранные пиксели по вертикали → единицы панели. ScreenToPanel
        // отображает позиции, но для scale-only рантайм-панели это ровно тот
        // масштаб, который нужен и для расстояний.
        private static float ToPanel(IPanel panel, float pixels)
            => Mathf.Max(0f, RuntimePanelUtils.ScreenToPanel(panel, new Vector2(0f, pixels)).y);
    }
}
