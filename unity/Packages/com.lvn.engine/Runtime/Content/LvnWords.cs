using System.Collections.Generic;

namespace Lvn.Content
{
    /// <summary>
    /// СЛОВАРЬ ОБОЛОЧКИ — откуда берётся ЛЮБАЯ подпись, которую движок пишет
    /// на экране сам.
    ///
    /// <para>Роль нашлась не поиском дублей, а закономерностью: три подряд
    /// выделенные роли (Ценник, Имя игрока, Титровальщик) вскрыли одну и ту же
    /// болезнь — русские слова, зашитые в движок. «Кристаллы», «Гость»,
    /// «Глава» лежали в коде и не переопределялись ничем, то есть любая другая
    /// новелла получала их насильно.</para>
    ///
    /// <para>Причина глубже отдельных строк: у подписей нет ВЛАДЕЛЬЦА. Часть
    /// берётся из манифеста с русским умолчанием (<c>nav_home ?? "Главная"</c>),
    /// часть — с английским (<c>equip_text ?? "Equip"</c>), а целые экраны
    /// (ежедневные награды, профиль) пишут русским прямо в коде и не
    /// переопределяются вовсе. Три правила на одну работу — второй признак из
    /// списка выше.</para>
    ///
    /// <para>Ответственность: по ключу дать слово. Порядок один и тот же
    /// всегда: что сказала новелла (<c>ui.words</c>) → что просит вызывающий
    /// как умолчание → английское слово движка. Перевод НЕ здесь: каталоги
    /// локали живут у своего механизма и подставляются новеллой; словарь лишь
    /// не мешает ей это сделать.</para>
    ///
    /// <para>Границы. Словарь — про подписи ДВИЖКА (кнопки оболочки, заголовки
    /// её экранов). Текст новеллы — реплики, названия глав, имена предметов —
    /// приходит из контента и через словарь не проходит.</para>
    ///
    /// <para>ЖИВЁТ В НИЖНЕМ СЛОЕ намеренно. Сперва он лежал среди интерфейса, но
    /// его спрашивают и оттуда, и из модели контента (Титровальщик — имя главы),
    /// а нижняя сборка верхнюю не видит. Слова — инфраструктура текста, а не
    /// украшение экрана.</para>
    /// </summary>
    public static class LvnWords
    {
        private static Dictionary<string, string> _words;

        /// <summary>Принять словарь новеллы (<c>ui.words</c>): ключ → слово.
        /// Зовётся при загрузке манифеста, до первого показа экрана.</summary>
        public static void Learn(Dictionary<string, string> words)
        {
            _words = words == null || words.Count == 0
                ? null
                : new Dictionary<string, string>(words, System.StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Слово по ключу. <paramref name="fallback"/> — что показать, если
        /// новелла ключ не назвала: обычно английское умолчание движка.
        /// </summary>
        public static string Of(string key, string fallback)
        {
            if (!string.IsNullOrEmpty(key) && _words != null
                && _words.TryGetValue(key, out var w) && !string.IsNullOrEmpty(w))
                return w;
            return fallback;
        }

        /// <summary>То же, но с подстановкой одного числа: «День {0}» → «День 3».
        /// Порядок слов в разных языках разный, поэтому число подставляется
        /// шаблоном, а не склеиванием.</summary>
        public static string Of(string key, string fallback, object arg0)
        {
            var pattern = Of(key, fallback);
            return string.IsNullOrEmpty(pattern) ? pattern
                 : pattern.Contains("{0}") ? string.Format(pattern, arg0)
                 : pattern + " " + arg0;
        }

        /// <summary>
        /// СЛОВО ПРИ ЧИСЛЕ: «1 глава», «2 главы», «5 глав».
        ///
        /// <para>Правило склонения было вписано в экран профиля прямо кодом —
        /// со славянскими остатками от 11 до 14 и русскими формами в
        /// <c>switch</c>. Английской новелле оно даёт «5 глава», и обойти его
        /// автор не может ничем.</para>
        ///
        /// <para>Форм не одна и не всегда три: язык выбирает СЕБЕ правило тем,
        /// сколько форм назвал автор. Дал <c>.few</c> — считаем язык славянским
        /// и применяем остатки; не дал — простое «один против прочих». Так
        /// движку не нужно знать список языков мира.</para>
        /// </summary>
        public static string Plural(string key, long n, string one, string other)
        {
            string w1 = Of(key + ".one", null);
            string few = Of(key + ".few", null);
            string many = Of(key + ".many", null);
            if (w1 != null && few != null && many != null)
            {
                long lastTwo = n % 100;
                if (lastTwo >= 11 && lastTwo <= 14) return many;
                switch (n % 10)
                {
                    case 1: return w1;
                    case 2: case 3: case 4: return few;
                    default: return many;
                }
            }
            return n == 1 ? Of(key + ".one", one) : Of(key + ".other", other);
        }
    }
}
