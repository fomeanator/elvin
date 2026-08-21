using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ЦВЕТ ИЗ СТРОКИ — один дом на весь движок.
    ///
    /// <para>Раньше разбор цвета жил в пяти местах: у слоя `ui`, у стека
    /// эффектов кадра, у спрайтовых эффектов, у темы и здесь. Пять реализаций
    /// одного понятия неизбежно расходятся — ровно так проценты в координатах
    /// понимались деревом `ui` и молча терялись у актёров.</para>
    ///
    /// <para>Понятий на самом деле два: <see cref="Parse"/> — это «шестнадцать
    /// цифр из манифеста», а <see cref="Token"/> — «цвет действующей темы по
    /// имени, иначе шестнадцать цифр». Второе знает про тему, первое нет.</para>
    /// </summary>
    public static class UiColor
    {
        /// <summary>Hex вида #rrggbb / #rrggbbaa. Мусор и пустота — fallback.</summary>
        public static Color Parse(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            var s = hex[0] == '#' ? hex.Substring(1) : hex;
            // Unity's util accepts 6/8-digit (and #-prefixed); normalise to that.
            if (ColorUtility.TryParseHtmlString("#" + s, out var c)) return c;
            return fallback;
        }

        /// <summary>
        /// Имя токена действующей темы (<c>accent</c>, <c>panel</c>, <c>veil</c>…)
        /// или hex. Неизвестное имя — fallback И предупреждение: опечатка иначе
        /// молча даёт прозрачный цвет, и «нарисовалось не то» приходится искать
        /// глазами.
        /// </summary>
        public static Color Token(string name, Color fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            switch (name)
            {
                case "bg": return LvnTokens.Bg;
                case "surface": return LvnTokens.Surface;
                case "surface_hi": return LvnTokens.SurfaceHi;
                case "panel": return LvnTokens.PanelBg;
                case "text": return LvnTokens.Text;
                case "dim": return LvnTokens.TextDim;
                case "accent": return LvnTokens.Accent;
                case "on_accent": return LvnTokens.OnAccent;
                case "gold": return LvnTokens.Gold;
                case "warn": return LvnTheme.Current.Warn;
                case "border": return LvnTokens.Border;
                case "veil": return LvnTokens.Scrim;
                case "clear": return new Color(0, 0, 0, 0);
            }
            if (ColorUtility.TryParseHtmlString(name, out var c)) return c;
            if (!name.Contains("{"))
                Debug.LogWarning($"[lvn-ui] неизвестный цвет \"{name}\" — беру цвет по умолчанию");
            return fallback;
        }

        /// <summary>Цвет из поля команды. Пусто/мусор — текущее значение.</summary>
        public static Color FromCmd(JObject cmd, string key, Color current)
        {
            var text = (string)cmd?[key];
            return string.IsNullOrEmpty(text) ? current : Parse(text, current);
        }
    }
}
