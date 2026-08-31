using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// КАК ЧИТАЕТСЯ ЗНАЧЕНИЕ АВТОРСКОЙ ЗАПИСИ — длина, отступ, кегль, цвет,
    /// «да/нет», имя значка.
    ///
    /// <para>Это МЕХАНИЗМ, а не содержание: он не знает, что строится, — только
    /// как понять написанное. Жил он вперемешку со сборкой дерева
    /// (<see cref="LvnUiLayer"/>), и сборка получалась вдвое длиннее, чем есть.
    /// Разбор при этом самостоятелен: у него свои правила и своя цена ошибки,
    /// и каждое такое правило уже стоило дефекта — <c>w=auto</c>, обещанный
    /// языком, разбирался в ноль и схлопывал элемент в невидимую точку;
    /// <c>pad=3</c> означало три пикселя вместо третьей ступени шкалы темы;
    /// свой разбор цвета делал так, что один и тот же <c>accent</c> значил
    /// разное в разных слоях.</para>
    ///
    /// <para>Сами числа, слова «да/нет» и цвета разбирают ОБЩИЕ дома
    /// (<see cref="Lvn.LvnNum"/>, <see cref="Lvn.LvnBool"/>,
    /// <see cref="UiColor"/>). Здесь остаётся то, что принадлежит разметке:
    /// выбор единицы (у стилей UI Toolkit проценты и пиксели — разные типы),
    /// ступени темы и судьба непонятого значения.</para>
    /// </summary>
    internal static class LvnUiValues
    {
        internal static bool Truthy(JToken t) => t != null && Truthy(t.ToString());

        internal static readonly Color Color32Clear = new Color(0, 0, 0, 0);

        internal enum Unit { Px, Percent, Auto }

        // Длина: число или процент. САМ разбор — в общем доме (LvnNum), здесь
        // остаётся только выбор единицы: у стилей UI Toolkit проценты и
        // пиксели разные типы, а у координат сцены процент — просто доля.
        internal static float Len(JToken t, out Unit u)
        {
            u = Unit.Px;
            if (t == null) return 0f;
            var s = t.ToString().Trim();
            // «auto» обещано языком (`w=auto`), а разбирался он как мусор: ноль,
            // то есть элемент схлопывался в невидимую точку. Отдельная единица
            // честнее подмены числом — решает её тот, кто ставит стиль.
            if (string.Equals(s, "auto", System.StringComparison.OrdinalIgnoreCase))
            { u = Unit.Auto; return 0f; }
            if (s.EndsWith("%"))
            {
                u = Unit.Percent;
                return Lvn.LvnNum.Parse(s.Substring(0, s.Length - 1), 0f);
            }
            return Lvn.LvnNum.Parse(t, 0f);
        }

        internal static void SetLen(Action<StyleLength> set, float v, Unit u)
        {
            // «auto» — не число, а ключевое слово стиля: пусть раскладка сама
            // решит размер по содержимому.
            if (u == Unit.Auto) { set(new StyleLength(StyleKeyword.Auto)); return; }
            set(u == Unit.Percent ? Length.Percent(v) : (Length)v);
        }

        // Есть ли в значении живая часть. Статические размеры кладём один раз
        // в ApplyLayout — заводить на них привязку значит опрашивать зря.
        internal static bool Live(JToken t) => t != null && t.ToString().Contains("{");

        internal static float Len(string s, out Unit u) => Len((JToken)s, out u);

        internal static float Num(string s, float def) => Num((JToken)s, def);

        // Словарь общий (Lvn.LvnBool), а вот судьба НЕПОНЯТОГО значения здесь
        // своя и намеренная: в разметке непустая строка исторически значит
        // «свойство задано», поэтому незнакомое слово — согласие, а не
        // умолчание. Это единственное осмысленное расхождение из шести.
        internal static bool Truthy(string s)
            => !string.IsNullOrEmpty(s) && Lvn.LvnBool.Of(s, true);

        // Кегль по ИМЕНИ ступени, а не числом: одинаковые вещи на разных
        // экранах обязаны быть одного размера. Число тоже принимается — но
        // тогда за разнобой отвечает автор, а не тема.
        internal static float TextSize(JToken t)
        {
            switch (t?.ToString())
            {
                case "xs": return LvnTokens.TextXs;
                case "sm": return LvnTokens.TextSm;
                case "base": return LvnTokens.TextBase;
                case "lg": return LvnTokens.TextLg;
                case "xl": return LvnTokens.TextXl;
                case "display": return LvnTokens.TextDisplay;
            }
            return Num(t, LvnTokens.TextBase);
        }

        // Отступ по ступени шкалы: pad=3 — это Space3 темы, а не «три пикселя».
        // Проценты и пиксели по-прежнему работают, ступень выбирается только
        // для целых 1…6 — их писать удобнее всего, и они самые частые.
        internal static float Step(JToken t, out Unit unit)
        {
            unit = Unit.Px;
            var raw = t?.ToString();
            switch (raw)
            {
                case "1": return LvnTokens.Space1;
                case "2": return LvnTokens.Space2;
                case "3": return LvnTokens.Space3;
                case "4": return LvnTokens.Space4;
                case "5": return LvnTokens.Space5;
                case "6": return LvnTokens.Space6;
            }
            return Len(t, out unit);
        }

        internal static float Num(JToken t, float def) => Lvn.LvnNum.Parse(t, def);

        internal static Color Color(JToken t, Color def) => Color(t?.ToString(), def);

        /// <summary>Цвет из литерала или ИЗ ТОКЕНА ТЕМЫ. Токены важнее
        /// удобства: иначе игровой интерфейс останется единственным местом,
        /// живущим своей палитрой, и смена темы его не тронет.</summary>
        // Цвет — из общего дома (UiColor.Token): имена токенов темы плюс hex.
        // Своя копия здесь и была тем, из-за чего один и тот же `accent` мог
        // означать разное в разных слоях.
        internal static Color Color(string s, Color def) => UiColor.Token(s, def);

        /// <summary>Значок по ИМЕНИ. Незнакомое имя — звезда-заглушка: у автора
        /// опечатка, а у игрока должно остаться что-то видимое.
        ///
        /// <para>ЧИСЛО ИМЕНЕМ НЕ СЧИТАЕТСЯ. Разбор перечисления принимает и
        /// числа: `icon name=5` молча давал шестой по счёту значок, а
        /// `name=999` — значение, которого в перечислении нет вовсе, и на
        /// экране оставалась ДЫРКА в ряду — ни значка, ни заглушки, ни
        /// жалобы.</para></summary>
        internal static LvnIcon IconByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return LvnIcon.Star;
            if (int.TryParse(name, out _)) return LvnIcon.Star;
            return Enum.TryParse<LvnIcon>(name, true, out var ic) && Enum.IsDefined(typeof(LvnIcon), ic)
                ? ic : LvnIcon.Star;
        }
    }
}
