using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// РЯД ВАРИАНТОВ — выбор одного из нескольких, который не уезжает за край
    /// экрана.
    ///
    /// <para>Пилюли выбора собирались вручную пять раз: качество арта, трек
    /// меню, «вкл/выкл», язык истории, вкладки настроек. Каждый раз — контейнер
    /// в строку, кнопки с отступом, список для подсветки и своя функция
    /// <c>Highlight</c>. Совпадало всё, кроме одной строки: перенос по нехватке
    /// ширины стоял ровно у ОДНОГО (трек меню — там вариантов много, и за это
    /// уже было заплачено).</para>
    ///
    /// <para>Цену заплатили остальные. Третья кнопка качества арта («1K») на
    /// телефоне партнёра оказалась за краем экрана, и добраться до неё было
    /// нельзя: строка настроек не прокручивается вбок и не должна. Игрок при
    /// этом видит не обрезанный ряд, а ДВА варианта вместо трёх — то есть не
    /// узнаёт, что третий существует. Язык истории ждала та же судьба: кнопок
    /// там столько, сколько каталогов у новеллы.</para>
    ///
    /// <para>Поэтому правило дома одно и невыключаемое: варианты переносятся на
    /// следующую строку и никогда не сжимаются. Лучше ряд в две строки, чем
    /// вариант, которого для игрока не существует.</para>
    ///
    /// <para>Вид кнопки дом не выбирает: палитру приносит новелла, и стиль
    /// приходит делегатом от экрана. Дом отвечает за состав, расстановку,
    /// перенос и за то, что подсветка обновится у ВСЕХ кнопок ряда — раньше это
    /// был отдельный список и отдельный цикл в каждом месте.</para>
    /// </summary>
    public static class LvnSegment
    {
        /// <summary>Собрать ряд вариантов.</summary>
        /// <param name="options">варианты в авторском порядке</param>
        /// <param name="label">подпись варианта</param>
        /// <param name="isCurrent">выбран ли вариант СЕЙЧАС — спрашивается при
        /// каждой перерисовке, поэтому ряд не хранит выбор и не может с ним
        /// разойтись</param>
        /// <param name="pick">что сделать при нажатии; подсветка обновится сама</param>
        /// <param name="style">вид кнопки: (кнопка, выбран ли)</param>
        public static VisualElement Of<T>(IEnumerable<T> options, Func<T, string> label,
                                          Func<T, bool> isCurrent, Action<T> pick,
                                          Action<Button, bool> style)
        {
            var seg = new VisualElement();
            seg.style.flexDirection = FlexDirection.Row;
            seg.style.flexWrap = Wrap.Wrap;              // НЕВЫКЛЮЧАЕМО: см. сводку
            seg.style.justifyContent = Justify.FlexEnd;
            if (options == null) return seg;

            var made = new List<(Button b, T value)>();
            void Highlight()
            {
                foreach (var (b, v) in made) style?.Invoke(b, isCurrent == null || isCurrent(v));
            }

            foreach (var opt in options)
            {
                var value = opt;
                var b = new Button { text = label != null ? label(value) : value?.ToString() ?? "" };
                b.style.marginLeft = 6;
                b.style.marginBottom = 6;   // перенос без него слипается со следующей строкой
                b.style.flexShrink = 0;     // сжатая кнопка теряет подпись раньше, чем ряд — ширину
                b.clicked += () => { pick?.Invoke(value); Highlight(); };
                made.Add((b, value));
                seg.Add(b);
            }
            Highlight();
            return seg;
        }
    }
}
