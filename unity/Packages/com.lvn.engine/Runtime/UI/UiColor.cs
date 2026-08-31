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
        /// <summary>
        /// Hex вида #rrggbb / #rrggbbaa. Мусор и пустота — fallback, МОЛЧА.
        ///
        /// <para>Это разбор «шестнадцати цифр», а не словарь: им пользуется
        /// СБОРКА ТЕМЫ, и звать оттуда словарь нельзя — словарь спрашивает
        /// цвет у действующей темы, а она в этот момент ещё строится.</para>
        ///
        /// <para>Всё остальное авторское — через <see cref="Named"/>. Сто три
        /// поля манифеста читались этим разбором, и `title_color: "accent"`
        /// молча не срабатывал: в скрипте то же слово работало, в манифесте —
        /// нет, хотя пишет их один человек.</para>
        /// </summary>
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
        /// ЦВЕТ ПО ИМЕНИ — ОДИН СЛОВАРЬ НА ВЕСЬ ЯЗЫК.
        ///
        /// <para>Их было три, и у каждого свой набор слов. Дерево <c>ui</c>
        /// знало токены темы, но не знало ни <c>warm</c>, ни <c>sepia</c>.
        /// Команды кадра (<c>tint</c>, <c>flash</c>) знали настроения, но не
        /// знали <c>accent</c>. А поля команд (<c>fx ink_color=</c>) не знали
        /// ни того, ни другого — только шестнадцать цифр и имена HTML. Автор
        /// писал одно слово в трёх местах и в двух из них получал молчание:
        /// «эффект не сработал», хотя сработал — цвета просто не нашли.</para>
        ///
        /// <para>Порядок слов не случаен. Имена движка стоят ПЕРЕД HTML
        /// намеренно: «green» в HTML — тёмно-зелёный (#008000), а в движке
        /// яркий (0,1,0), и молча сменить его значило бы перекрасить уже
        /// написанные главы. Остальные семь совпадают, но лежат рядом, чтобы
        /// набор читался как один список.</para>
        ///
        /// <para>Регистр не важен: <c>Accent</c> и <c>accent</c> — одно слово.
        /// Раньше первый вариант тихо уходил в HTML и не находился там.</para>
        /// </summary>
        public static Color Named(string name, Color fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            switch (name.ToLowerInvariant())
            {
                // Токены действующей темы.
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
                // Имена движка (см. про «green» выше).
                case "white": return Color.white;
                case "black": return Color.black;
                case "red": return Color.red;
                case "blue": return Color.blue;
                case "green": return Color.green;
                case "yellow": return Color.yellow;
                case "cyan": return Color.cyan;
                case "magenta": return Color.magenta;
                // Мнемоники настроения — готовые оттенки, которые зовут словом.
                case "cold":
                case "tint_cold": return new Color(0.6f, 0.7f, 1f, 1f);
                case "warm":
                case "tint_warm": return new Color(1f, 0.85f, 0.7f, 1f);
                case "sepia": return new Color(0.76f, 0.6f, 0.42f, 1f);
            }
            // Всё остальное — общий разбор: «#rrggbb», «#rrggbbaa», «ff0000»
            // без решётки и прочие имена HTML.
            if (TryParse(name, out var c)) return c;
            // Опечатка автора — жалоба, а не молчаливая подмена: без неё
            // «нарисовалось не то» приходится искать глазами по всему скрипту.
            // Незакрытая подстановка — не опечатка: её ещё не подставили.
            if (!name.Contains("{"))
                Debug.LogWarning($"[lvn-ui] неизвестный цвет \"{name}\" — беру цвет по умолчанию");
            return fallback;
        }

        /// <summary>Прежнее имя двери: токен темы или hex. Теперь окно в общий
        /// словарь — набор слов у цвета один, где бы его ни писали.</summary>
        /// <summary>Тот же цвет, но прозрачнее или плотнее. Собиралось по
        /// месту из трёх составляющих — а «тот же цвет с другой плотностью» и
        /// есть работа дома цвета.</summary>
        public static Color WithAlpha(Color c, float alpha) => new Color(c.r, c.g, c.b, alpha);

        /// <summary>Светлее / темнее на долю — к белому и к чёрному. Пара жила
        /// приватной у витрины, хотя ею красят и карточки, и кромки.</summary>
        public static Color Lighter(Color c, float amount) => Color.Lerp(c, Color.white, amount);

        public static Color Darker(Color c, float amount) => Color.Lerp(c, Color.black, amount);

        public static Color Token(string name, Color fallback) => Named(name, fallback);

        /// <summary>
        /// Цвет из поля команды. Пусто — текущее значение молча; мусор —
        /// текущее значение И жалоба: строку писал автор, а опечатка в цвете
        /// иначе выглядит как «эффект не сработал».
        /// </summary>
        public static Color FromCmd(JObject cmd, string key, Color current)
        {
            var text = (string)cmd?[key];
            if (string.IsNullOrEmpty(text)) return current;
            // Через общий словарь: поле команды понимало ТОЛЬКО шестнадцать
            // цифр, и «ink_color=warm» молча оставляло прежний цвет, хотя
            // соседняя команда то же слово понимала.
            return Named(text, current);
        }
    }
}
