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
            => TryParse(hex, out var c) ? c : fallback;

        /// <summary>
        /// Разбор с ответом «получилось или нет». Отдельно от <see cref="Parse"/>,
        /// потому что «цвет вышел таким же, как был» и «цвет не вышел» — разные
        /// события, а по одному лишь результату они неразличимы.
        /// </summary>
        public static bool TryParse(string hex, out Color color)
        {
            color = default;
            if (string.IsNullOrEmpty(hex)) return false;
            // Сперва как есть — так проходят имена, которые знает Unity ("red").
            // Потом с решёткой — так проходит авторское "ff0000" без неё.
            if (ColorUtility.TryParseHtmlString(hex, out color)) return true;
            var s = hex[0] == '#' ? hex.Substring(1) : hex;
            return ColorUtility.TryParseHtmlString("#" + s, out color);
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

        /// <summary>
        /// Цвет из поля команды. Пусто — текущее значение молча; мусор —
        /// текущее значение И жалоба: строку писал автор, а опечатка в цвете
        /// иначе выглядит как «эффект не сработал».
        /// </summary>
        public static Color FromCmd(JObject cmd, string key, Color current)
        {
            var text = (string)cmd?[key];
            if (string.IsNullOrEmpty(text)) return current;
            if (TryParse(text, out var c)) return c;
            Debug.LogWarning($"[lvn-ui] {key}=\"{text}\" — не цвет, оставляю прежний");
            return current;
        }
    }
}
